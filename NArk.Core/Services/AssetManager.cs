using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Assets;
using NArk.Core.CoinSelector;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Helpers;
using NArk.Core.Transport;
using NArk.Core.Extensions;
using NBitcoin;
using CoinSelector_ICoinSelector = NArk.Core.CoinSelector.ICoinSelector;

namespace NArk.Core.Services;

public class AssetManager(
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    ICoinService coinService,
    IWalletProvider walletProvider,
    IContractService contractService,
    IClientTransport transport,
    CoinSelector_ICoinSelector coinSelector,
    ISafetyService safetyService,
    IIntentStorage intentStorage,
    IEnumerable<IEventHandler<PostCoinsSpendActionEvent>> postSpendEventHandlers,
    ILogger<AssetManager>? logger = null) : IAssetManager
{
    public async Task<IssuanceResult> IssueAsync(string walletId, IssuanceParams parameters,
        CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Issuing {Amount} new asset units for wallet {WalletId}", parameters.Amount, walletId);

        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

        var coins = await GetAvailableCoins(walletId, cancellationToken);

        // When issuing with a control asset, include the control asset VTXO as an input
        // and pass it through so the server can verify ownership via group index reference.
        ArkCoin? controlCoin = null;
        if (parameters.ControlAssetId is not null)
        {
            controlCoin = coins.FirstOrDefault(c =>
                c.Assets is { Count: > 0 } assets &&
                assets.Any(a => a.AssetId == parameters.ControlAssetId));
            if (controlCoin is null)
                throw new InvalidOperationException(
                    $"No VTXO found carrying control asset {parameters.ControlAssetId} in wallet {walletId}");
        }

        // Select BTC carrier coins. When we have a control asset, we need extra dust
        // for the control asset passthrough output.
        var btcCoins = coins.Where(c => c.Assets is null or { Count: 0 }).ToList();
        var dustNeeded = controlCoin is not null ? serverInfo.Dust * 2 : serverInfo.Dust;
        var controlCoinBtc = controlCoin?.TxOut.Value ?? Money.Zero;
        var btcToSelect = dustNeeded - controlCoinBtc;

        List<ArkCoin> selectedCoins;
        if (btcToSelect > Money.Zero)
        {
            var btcSelected = coinSelector.SelectCoins(btcCoins, btcToSelect, serverInfo.Dust, 0);
            selectedCoins = controlCoin is not null
                ? [controlCoin, .. btcSelected]
                : [.. btcSelected];
        }
        else
        {
            selectedCoins = [controlCoin!];
        }

        try
        {
            // Derive a receive contract for the new asset carrier output
            var assetContract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive,
                cancellationToken: cancellationToken);
            var assetOutput = new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, assetContract.GetArkAddress());

            var outputsList = new List<ArkTxOut> { assetOutput };

            // When using a control asset, add a passthrough output for the control asset
            if (controlCoin is not null)
            {
                var inputContracts = selectedCoins.Select(c => c.Contract).ToArray();
                var controlPassthroughContract = await contractService.DeriveContract(walletId,
                    NextContractPurpose.SendToSelf, inputContracts, cancellationToken: cancellationToken);
                outputsList.Add(new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust,
                    controlPassthroughContract.GetArkAddress()));
            }

            // Build BTC change output if needed
            var totalIn = selectedCoins.Sum(c => c.TxOut.Value);
            var btcUsedForOutputs = Money.Satoshis(outputsList.Sum(o => o.Value));
            var change = totalIn - btcUsedForOutputs;

            if (change >= serverInfo.Dust)
            {
                var inputContracts = selectedCoins.Select(c => c.Contract).ToArray();
                var changeContract = await contractService.DeriveContract(walletId, NextContractPurpose.SendToSelf,
                    inputContracts, cancellationToken: cancellationToken);
                outputsList.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(change),
                    changeContract.GetArkAddress()));
            }

            var outputs = outputsList.ToArray();

            // Build issuance packet
            var metadata = parameters.Metadata?
                .Select(kv => AssetMetadata.Create(kv.Key, kv.Value))
                .ToList() ?? [];

            var groups = new List<AssetGroup>();

            if (controlCoin is not null)
            {
                // Find the input index of the control coin
                var controlInputIndex = (ushort)selectedCoins.IndexOf(controlCoin);
                var controlAsset = controlCoin.Assets!.First(a => a.AssetId == parameters.ControlAssetId);

                // Group 0: passthrough of the control asset (proves ownership)
                var passthroughGroup = AssetGroup.Create(
                    assetId: AssetId.FromString(parameters.ControlAssetId!),
                    controlAsset: null,
                    inputs: [AssetInput.Create(controlInputIndex, controlAsset.Amount)],
                    outputs: [AssetOutput.Create(1, controlAsset.Amount)],
                    metadata: []);
                groups.Add(passthroughGroup);

                // Group 1: issuance referencing the passthrough group by index
                var issuanceGroup = AssetGroup.Create(
                    assetId: null,
                    controlAsset: AssetRef.FromGroupIndex(0),
                    inputs: [],
                    outputs: [AssetOutput.Create(0, parameters.Amount)],
                    metadata: metadata);
                groups.Add(issuanceGroup);
            }
            else
            {
                // Simple issuance without control asset
                var issuanceGroup = AssetGroup.Create(
                    assetId: null,
                    controlAsset: null,
                    inputs: [],
                    outputs: [AssetOutput.Create(0, parameters.Amount)],
                    metadata: metadata);
                groups.Add(issuanceGroup);
            }

            var packet = Packet.Create(groups);

            // Submit the transaction
            var transactionBuilder =
                new TransactionHelpers.ArkTransactionBuilder(transport, safetyService, walletProvider, intentStorage);

            var tx = await transactionBuilder.ConstructAndSubmitArkTransaction(
                [.. selectedCoins], outputs, cancellationToken, packet.ToTxOut());
            var txHash = tx.GetGlobalTransaction().GetHash();

            logger?.LogInformation(
                "Asset issuance transaction {TxId} completed for wallet {WalletId}",
                txHash, walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(
                new PostCoinsSpendActionEvent([.. selectedCoins], txHash, tx, ActionState.Successful, null),
                cancellationToken: cancellationToken);

            // Derive AssetId from {txHash, issuanceGroupIndex}
            // When there's a control asset, the issuance group is at index 1 (after passthrough)
            var issuanceGroupIndex = controlCoin is not null ? (ushort)1 : (ushort)0;
            var assetId = AssetId.Create(txHash.ToString(), issuanceGroupIndex);
            return new IssuanceResult(txHash.ToString(), assetId.ToString());
        }
        catch (Exception ex)
        {
            logger?.LogError(0, ex, "Asset issuance failed for wallet {WalletId}", walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(
                new PostCoinsSpendActionEvent([.. selectedCoins], null, null,
                    ActionState.Failed, $"Asset issuance failed with ex: {ex}"),
                cancellationToken: cancellationToken);
            throw;
        }
    }

    public async Task<string> ReissueAsync(string walletId, ReissuanceParams parameters,
        CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Reissuing {Amount} units using control asset {AssetId} for wallet {WalletId}",
            parameters.Amount, parameters.AssetId, walletId);

        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);
        var coins = await GetAvailableCoins(walletId, cancellationToken);

        // Find a coin carrying the control asset
        var controlCoin = coins.FirstOrDefault(c =>
            c.Assets is { Count: > 0 } assets &&
            assets.Any(a => a.AssetId == parameters.AssetId));

        if (controlCoin is null)
            throw new InvalidOperationException(
                $"No VTXO found carrying control asset {parameters.AssetId} in wallet {walletId}");

        var controlAsset = controlCoin.Assets!.First(a => a.AssetId == parameters.AssetId);

        // We need BTC for: new asset output + control passthrough output
        var btcNeeded = serverInfo.Dust * 2;
        var selectedCoins = new List<ArkCoin> { controlCoin };

        if (controlCoin.TxOut.Value < btcNeeded)
        {
            var additionalCoins = coins
                .Where(c => c != controlCoin && (c.Assets is null or { Count: 0 }))
                .ToList();
            var extraCoins = coinSelector.SelectCoins(additionalCoins,
                btcNeeded - controlCoin.TxOut.Value, serverInfo.Dust, 0);
            selectedCoins.AddRange(extraCoins);
        }

        try
        {
            var totalIn = selectedCoins.Sum(c => c.TxOut.Value);
            var controlInputIndex = (ushort)0; // control coin is always first

            // Build outputs:
            // vout 0: new asset carrier (receives the newly issued asset)
            // vout 1: control asset passthrough (returns the control asset to self)
            // vout 2: BTC change (if any)
            var assetContract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive,
                cancellationToken: cancellationToken);
            var newAssetOutput = new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, assetContract.GetArkAddress());

            var inputContracts = selectedCoins.Select(c => c.Contract).ToArray();
            var controlPassthroughContract = await contractService.DeriveContract(walletId,
                NextContractPurpose.SendToSelf, inputContracts, cancellationToken: cancellationToken);
            var controlPassthroughOutput = new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust,
                controlPassthroughContract.GetArkAddress());

            var outputsList = new List<ArkTxOut> { newAssetOutput, controlPassthroughOutput };

            var change = totalIn - serverInfo.Dust * 2;
            if (change >= serverInfo.Dust)
            {
                var changeContract = await contractService.DeriveContract(walletId, NextContractPurpose.SendToSelf,
                    inputContracts, cancellationToken: cancellationToken);
                outputsList.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(change),
                    changeContract.GetArkAddress()));
            }

            var outputs = outputsList.ToArray();

            // Build the packet with two groups:
            // Group 0: passthrough — transfers the control asset from input to output (proves ownership)
            // Group 1: controlled issuance — references group 0 by index to authorize new asset creation
            var passthroughGroup = AssetGroup.Create(
                assetId: AssetId.FromString(parameters.AssetId),
                controlAsset: null,
                inputs: [AssetInput.Create(controlInputIndex, controlAsset.Amount)],
                outputs: [AssetOutput.Create(1, controlAsset.Amount)],
                metadata: []);

            var issuanceGroup = AssetGroup.Create(
                assetId: null,
                controlAsset: AssetRef.FromGroupIndex(0),
                inputs: [],
                outputs: [AssetOutput.Create(0, parameters.Amount)],
                metadata: []);

            var packet = Packet.Create([passthroughGroup, issuanceGroup]);

            // Submit the transaction
            var transactionBuilder =
                new TransactionHelpers.ArkTransactionBuilder(transport, safetyService, walletProvider, intentStorage);

            var tx = await transactionBuilder.ConstructAndSubmitArkTransaction(
                [.. selectedCoins], outputs, cancellationToken, packet.ToTxOut());
            var txHash = tx.GetGlobalTransaction().GetHash();

            logger?.LogInformation(
                "Asset reissuance transaction {TxId} completed for wallet {WalletId}",
                txHash, walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(
                new PostCoinsSpendActionEvent([.. selectedCoins], txHash, tx, ActionState.Successful, null),
                cancellationToken: cancellationToken);

            return txHash.ToString();
        }
        catch (Exception ex)
        {
            logger?.LogError(0, ex, "Asset reissuance failed for wallet {WalletId}", walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(
                new PostCoinsSpendActionEvent([.. selectedCoins], null, null,
                    ActionState.Failed, $"Asset reissuance failed with ex: {ex}"),
                cancellationToken: cancellationToken);
            throw;
        }
    }

    public async Task<string> BurnAsync(string walletId, BurnParams parameters,
        CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Burning {Amount} units of asset {AssetId} for wallet {WalletId}",
            parameters.Amount, parameters.AssetId, walletId);

        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);
        var coins = await GetAvailableCoins(walletId, cancellationToken);

        // Select coins carrying the target asset
        var assetCoins = coins
            .Where(c => c.Assets is { Count: > 0 } assets &&
                        assets.Any(a => a.AssetId == parameters.AssetId))
            .ToList();

        if (assetCoins.Count == 0)
            throw new InvalidOperationException(
                $"No VTXOs found carrying asset {parameters.AssetId} in wallet {walletId}");

        // Gather enough asset amount to cover the burn
        var selectedAssetCoins = new List<ArkCoin>();
        ulong gatheredAssetAmount = 0;
        foreach (var coin in assetCoins)
        {
            selectedAssetCoins.Add(coin);
            gatheredAssetAmount += coin.Assets!
                .Where(a => a.AssetId == parameters.AssetId)
                .Aggregate(0UL, (sum, a) => sum + a.Amount);
            if (gatheredAssetAmount >= parameters.Amount)
                break;
        }

        if (gatheredAssetAmount < parameters.Amount)
            throw new InvalidOperationException(
                $"Insufficient asset balance: have {gatheredAssetAmount}, need {parameters.Amount} of asset {parameters.AssetId}");

        try
        {
            var remainingAssetAmount = gatheredAssetAmount - parameters.Amount;
            var totalBtcIn = selectedAssetCoins.Sum(c => c.TxOut.Value);

            // Build asset inputs from the selected coins
            var assetInputs = new List<AssetInput>();
            for (var i = 0; i < selectedAssetCoins.Count; i++)
            {
                var coinAssetAmount = selectedAssetCoins[i].Assets!
                    .Where(a => a.AssetId == parameters.AssetId)
                    .Aggregate(0UL, (sum, a) => sum + a.Amount);
                assetInputs.Add(AssetInput.Create((ushort)i, coinAssetAmount));
            }

            // Build outputs:
            // If partial burn (remaining > 0), create an output for the remaining asset amount
            // Always create a BTC change output for the carrier BTC
            var inputContracts = selectedAssetCoins.Select(c => c.Contract).ToArray();
            var outputsList = new List<ArkTxOut>();
            var assetOutputs = new List<AssetOutput>();

            if (remainingAssetAmount > 0)
            {
                // vout 0: asset remainder output
                var assetContract = await contractService.DeriveContract(walletId,
                    NextContractPurpose.SendToSelf, inputContracts, cancellationToken: cancellationToken);
                outputsList.Add(new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust,
                    assetContract.GetArkAddress()));
                assetOutputs.Add(AssetOutput.Create(0, remainingAssetAmount));
            }

            // BTC change output
            var btcUsedForAssetOutput = remainingAssetAmount > 0 ? serverInfo.Dust : Money.Zero;
            var btcChange = totalBtcIn - btcUsedForAssetOutput;
            if (btcChange >= serverInfo.Dust)
            {
                var changeContract = await contractService.DeriveContract(walletId,
                    NextContractPurpose.SendToSelf, inputContracts, cancellationToken: cancellationToken);
                outputsList.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(btcChange),
                    changeContract.GetArkAddress()));
            }

            var outputs = outputsList.ToArray();

            // Build the burn packet: inputs have the full amount, outputs have the remaining amount
            // The difference (burnAmount) is destroyed
            var burnGroup = AssetGroup.Create(
                assetId: AssetId.FromString(parameters.AssetId),
                controlAsset: null,
                inputs: assetInputs,
                outputs: assetOutputs,
                metadata: []);

            var packet = Packet.Create([burnGroup]);

            // Submit the transaction
            var transactionBuilder =
                new TransactionHelpers.ArkTransactionBuilder(transport, safetyService, walletProvider, intentStorage);

            var tx = await transactionBuilder.ConstructAndSubmitArkTransaction(
                [.. selectedAssetCoins], outputs, cancellationToken, packet.ToTxOut());
            var txHash = tx.GetGlobalTransaction().GetHash();

            logger?.LogInformation(
                "Asset burn transaction {TxId} completed for wallet {WalletId}: burned {BurnAmount} of {AssetId}",
                txHash, walletId, parameters.Amount, parameters.AssetId);
            await postSpendEventHandlers.SafeHandleEventAsync(
                new PostCoinsSpendActionEvent([.. selectedAssetCoins], txHash, tx, ActionState.Successful, null),
                cancellationToken: cancellationToken);

            return txHash.ToString();
        }
        catch (Exception ex)
        {
            logger?.LogError(0, ex, "Asset burn failed for wallet {WalletId}", walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(
                new PostCoinsSpendActionEvent([.. selectedAssetCoins], null, null,
                    ActionState.Failed, $"Asset burn failed with ex: {ex}"),
                cancellationToken: cancellationToken);
            throw;
        }
    }

    private async Task<IReadOnlySet<ArkCoin>> GetAvailableCoins(string walletId,
        CancellationToken cancellationToken = default)
    {
        var vtxos = await vtxoStorage.GetVtxos(walletIds: [walletId], includeSpent: false,
            cancellationToken: cancellationToken);

        var scripts = vtxos.Select(v => v.Script).Distinct().ToArray();
        var contractByScript =
            (await contractStorage.GetContracts(walletIds: [walletId], scripts: scripts,
                cancellationToken: cancellationToken))
            .GroupBy(entity => entity.Script)
            .ToDictionary(g => g.Key, g => g.First());
        var vtxosByContracts = vtxos.GroupBy(v => contractByScript[v.Script]);

        HashSet<ArkCoin> coins = [];
        foreach (var vtxosByContract in vtxosByContracts)
        {
            foreach (var vtxo in vtxosByContract)
            {
                try
                {
                    coins.Add(await coinService.GetCoin(vtxosByContract.Key, vtxo, cancellationToken));
                }
                catch (AdditionalInformationRequiredException ex)
                {
                    logger?.LogDebug(0, ex,
                        "Skipping vtxo {TxId}:{Index} - requires additional information (likely VHTLC contract)",
                        vtxo.TransactionId, vtxo.TransactionOutputIndex);
                }
            }
        }

        return coins;
    }
}
