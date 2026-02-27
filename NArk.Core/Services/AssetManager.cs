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

        // Select BTC carrier coins for the new asset output
        var btcCoins = coins.Where(c => c.Assets is null or { Count: 0 }).ToList();
        var selectedCoins = coinSelector.SelectCoins(btcCoins, serverInfo.Dust, serverInfo.Dust, 0).ToList();

        try
        {
            // Derive a receive contract for the new asset carrier output
            var assetContract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive,
                cancellationToken: cancellationToken);
            var assetOutput = new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, assetContract.GetArkAddress());

            var outputsList = new List<ArkTxOut> { assetOutput };

            // Build BTC change output if needed
            var totalIn = selectedCoins.Sum(c => c.TxOut.Value);
            var change = totalIn - serverInfo.Dust;

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

            // Single issuance group. When controlled, reference the control asset by ID —
            // arkd looks it up in its database to verify it exists as a prior issuance.
            // No passthrough group needed; the control asset VTXO is not consumed.
            var issuanceGroup = AssetGroup.Create(
                assetId: null,
                controlAsset: parameters.ControlAssetId is not null
                    ? AssetRef.FromId(AssetId.FromString(parameters.ControlAssetId))
                    : null,
                inputs: [],
                outputs: [AssetOutput.Create(0, parameters.Amount)],
                metadata: metadata);

            var packet = Packet.Create([issuanceGroup]);

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

            // Derive AssetId from {txHash, groupIndex=0} — always the first (and only) group
            var assetId = AssetId.Create(txHash.ToString(), 0);
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

        // Select BTC carrier coins for the new asset output
        var btcCoins = coins.Where(c => c.Assets is null or { Count: 0 }).ToList();
        var selectedCoins = coinSelector.SelectCoins(btcCoins, serverInfo.Dust, serverInfo.Dust, 0).ToList();

        try
        {
            // Derive a receive contract for the new asset carrier output
            var assetContract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive,
                cancellationToken: cancellationToken);
            var newAssetOutput = new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, assetContract.GetArkAddress());

            var outputsList = new List<ArkTxOut> { newAssetOutput };

            var totalIn = selectedCoins.Sum(c => c.TxOut.Value);
            var change = totalIn - serverInfo.Dust;
            if (change >= serverInfo.Dust)
            {
                var inputContracts = selectedCoins.Select(c => c.Contract).ToArray();
                var changeContract = await contractService.DeriveContract(walletId, NextContractPurpose.SendToSelf,
                    inputContracts, cancellationToken: cancellationToken);
                outputsList.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(change),
                    changeContract.GetArkAddress()));
            }

            var outputs = outputsList.ToArray();

            // Single issuance group referencing the control asset by ID.
            // arkd verifies the control asset exists as a prior issuance in its database.
            // The control asset VTXO is not consumed — it remains available for future reissuances.
            var issuanceGroup = AssetGroup.Create(
                assetId: null,
                controlAsset: AssetRef.FromId(AssetId.FromString(parameters.AssetId)),
                inputs: [],
                outputs: [AssetOutput.Create(0, parameters.Amount)],
                metadata: []);

            var packet = Packet.Create([issuanceGroup]);

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
