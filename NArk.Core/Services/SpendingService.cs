using Microsoft.Extensions.Logging;
using NArk.Abstractions;
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

public class SpendingService(
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    ICoinService coinService,
    IWalletProvider walletProvider,
    IContractService paymentService,
    IClientTransport transport,
    CoinSelector_ICoinSelector coinSelector,
    ISafetyService safetyService,
    IIntentStorage intentStorage,
    IEnumerable<IEventHandler<PostCoinsSpendActionEvent>> postSpendEventHandlers,
    ILogger<SpendingService>? logger = null) : ISpendingService
{
    public SpendingService(IVtxoStorage vtxoStorage,
        IContractStorage contractStorage,
        IWalletProvider walletProvider,
        ICoinService coinService,
        IContractService paymentService,
        IClientTransport transport,
        CoinSelector_ICoinSelector coinSelector,
        ISafetyService safetyService,
        IIntentStorage intentStorage)
        : this(vtxoStorage, contractStorage, coinService, walletProvider, paymentService, transport, coinSelector,
            safetyService, intentStorage, [], null)
    {
    }

    public SpendingService(IVtxoStorage vtxoStorage,
        IContractStorage contractStorage,
        IWalletProvider walletProvider,
        ICoinService coinService,
        IContractService paymentService,
        IClientTransport transport,
        CoinSelector_ICoinSelector coinSelector,
        ISafetyService safetyService,
        IIntentStorage intentStorage,
        ILogger<SpendingService> logger)
        : this(vtxoStorage, contractStorage, coinService, walletProvider, paymentService, transport, coinSelector,
            safetyService, intentStorage, [], logger)
    {
    }

    public async Task<uint256> Spend(string walletId, ArkCoin[] inputs, ArkTxOut[] outputs,
        CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Spending {InputCount} inputs with {OutputCount} outputs for wallet {WalletId}", inputs.Length,
            outputs.Length, walletId);
        try
        {
            var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

            var totalInput = inputs.Sum(x => x.TxOut.Value);

            var outputsSumInSatoshis = outputs.Sum(o => o.Value);

            // Check if any output is explicitly subdust (the user wants to send subdust amount)
            var hasExplicitSubdustOutput = outputs.Count(o => o.Value < serverInfo.Dust);

            var change = totalInput - outputsSumInSatoshis;

            // Only derive a new change address if we actually need change
            // This is important for HD wallets as it consumes a derivation index
            ArkAddress? changeAddress = null;
            var needsChange = change >= serverInfo.Dust ||
                              (change > 0L && (hasExplicitSubdustOutput + 1) <= TransactionHelpers.MaxOpReturnOutputs);

            if (needsChange)
            {
                // Pass input contracts for potential descriptor recycling (avoids HD index bloat)
                // set inactive so that the postspend event polls and not have a contract constantly listening
                var inputContracts = inputs.Select(i => i.Contract).ToArray();
                changeAddress = (await paymentService.DeriveContract(walletId, NextContractPurpose.SendToSelf, 
                    inputContracts, cancellationToken: cancellationToken, activityState:ContractActivityState.Inactive)).GetArkAddress();
            }

            // Add change output if it's at or above the dust threshold
            if (change >= serverInfo.Dust)
            {
                outputs =
                [
                    ..outputs,
                    new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(change), changeAddress!)
                ];
            }
            else if (change > 0 && (hasExplicitSubdustOutput + 1) <= TransactionHelpers.MaxOpReturnOutputs)
            {
                outputs = [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(change), changeAddress!), .. outputs];
            }

            // Build asset packet if any inputs or outputs carry assets
            var assetPacketOutput = BuildAssetPacket(inputs, outputs);

            var transactionBuilder =
                new TransactionHelpers.ArkTransactionBuilder(transport, safetyService, walletProvider, intentStorage);

            var tx = await transactionBuilder.ConstructAndSubmitArkTransaction(inputs, outputs, cancellationToken,
                assetPacketOutput);
            var txId = tx.GetGlobalTransaction().GetHash();
            logger?.LogInformation("Spend transaction {TxId} completed successfully for wallet {WalletId}", txId,
                walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(new PostCoinsSpendActionEvent([.. inputs], txId, tx,
                ActionState.Successful, null), cancellationToken: cancellationToken);

            return txId;
        }
        catch (Exception ex)
        {
            logger?.LogError(0, ex, "Spend transaction failed for wallet {WalletId}", walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(new PostCoinsSpendActionEvent([.. inputs], null, null,
                ActionState.Failed, $"Spending coins failed with ex: {ex}"), cancellationToken: cancellationToken);

            throw;
        }
    }

    public async Task<IReadOnlySet<ArkCoin>> GetAvailableCoins(string walletId,
        CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Getting available coins for wallet {WalletId}", walletId);


        var vtxos = await vtxoStorage.GetVtxos(walletIds: [walletId], includeSpent: false,
            cancellationToken: cancellationToken);

        var scripts = vtxos.Select(v => v.Script).Distinct().ToArray();
        var contractByScript =
            (await contractStorage.GetContracts(walletIds: [walletId], scripts: scripts,
                cancellationToken: cancellationToken))
            .ToDictionary(entity => entity.Script);
        var vtxosByContracts =
            vtxos
                .GroupBy(v => contractByScript[v.Script]);

        HashSet<ArkCoin> coins = [];
        foreach (var vtxosByContract in vtxosByContracts)
        {
            foreach (var vtxo in vtxosByContract)
            {
                try
                {
                    coins.Add(
                        await coinService.GetCoin(vtxosByContract.Key, vtxo, cancellationToken));
                }
                catch (AdditionalInformationRequiredException ex)
                {
                    logger?.LogDebug(0, ex,
                        "Skipping vtxo {TxId}:{Index} - requires additional information (likely VHTLC contract)",
                        vtxo.TransactionId, vtxo.TransactionOutputIndex);
                }
            }
        }

        logger?.LogDebug("Found {CoinCount} available coins for wallet {WalletId}", coins.Count, walletId);
        return coins;
    }

    public async Task<uint256> Spend(string walletId, ArkTxOut[] outputs, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Spending with automatic coin selection for wallet {WalletId} with {OutputCount} outputs",
            walletId, outputs.Length);
        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

        var outputsSumInSatoshis = outputs.Sum(o => o.Value);

        // Check if any output is explicitly subdust (the user wants to send subdust amount)
        var hasExplicitSubdustOutput = outputs.Count(o => o.Value < serverInfo.Dust);

        var coins = await GetAvailableCoins(walletId, cancellationToken);

        // Extract asset requirements from outputs for asset-aware coin selection
        var assetRequirements = ExtractAssetRequirements(outputs);
        var selectedCoins = assetRequirements.Count > 0
            ? coinSelector.SelectCoins([.. coins], outputsSumInSatoshis, assetRequirements, serverInfo.Dust,
                hasExplicitSubdustOutput)
            : coinSelector.SelectCoins([.. coins], outputsSumInSatoshis, serverInfo.Dust,
                hasExplicitSubdustOutput);
        logger?.LogDebug("Selected {SelectedCount} coins for spending", selectedCoins.Count);

        try
        {
            var totalInput = selectedCoins.Sum(x => x.TxOut.Value);
            var change = totalInput - outputsSumInSatoshis;

            // Only derive a new change address if we actually need change
            // This is important for HD wallets as it consumes a derivation index
            ArkAddress? changeAddress = null;
            var needsChange = change >= serverInfo.Dust ||
                              (change > 0L && (hasExplicitSubdustOutput + 1) <= TransactionHelpers.MaxOpReturnOutputs);

            if (needsChange)
            {
                // Pass input contracts for potential descriptor recycling (avoids HD index bloat)
                var inputContracts = selectedCoins.Select(c => c.Contract).ToArray();
                changeAddress = (await paymentService.DeriveContract(walletId, NextContractPurpose.SendToSelf,
                    inputContracts, cancellationToken: cancellationToken)).GetArkAddress();
            }

            // Add change output if it's at or above the dust threshold
            if (change >= serverInfo.Dust)
            {
                outputs =
                [
                    ..outputs,
                    new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(change), changeAddress!)
                ];
            }
            else if (change > 0 && (hasExplicitSubdustOutput + 1) <= TransactionHelpers.MaxOpReturnOutputs)
            {
                outputs = [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(change), changeAddress!), .. outputs];
            }

            // Build asset packet if any inputs or outputs carry assets
            var assetPacketOutput = BuildAssetPacket(selectedCoins, outputs);

            var transactionBuilder =
                new TransactionHelpers.ArkTransactionBuilder(transport, safetyService, walletProvider, intentStorage);

            var tx = await transactionBuilder.ConstructAndSubmitArkTransaction(selectedCoins, outputs,
                cancellationToken, assetPacketOutput);
            var txId = tx.GetGlobalTransaction().GetHash();
            logger?.LogInformation(
                "Spend transaction {TxId} completed successfully for wallet {WalletId} with automatic coin selection",
                txId, walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(new PostCoinsSpendActionEvent(coins.ToArray(), txId, tx,
                ActionState.Successful, null), cancellationToken: cancellationToken);

            return txId;
        }
        catch (Exception ex)
        {
            logger?.LogError(0, ex, "Spend transaction with automatic coin selection failed for wallet {WalletId}",
                walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(new PostCoinsSpendActionEvent(coins.ToArray(), null, null,
                    ActionState.Failed, $"Spending selected coins failed with ex: {ex}"),
                cancellationToken: cancellationToken);

            throw;
        }
    }

    private static List<AssetRequirement> ExtractAssetRequirements(ArkTxOut[] outputs)
    {
        var totals = new Dictionary<string, ulong>();
        foreach (var output in outputs)
        {
            if (output.Assets is not { Count: > 0 } assets) continue;
            foreach (var asset in assets)
                totals[asset.AssetId] = totals.GetValueOrDefault(asset.AssetId) + asset.Amount;
        }
        return totals.Select(kv => new AssetRequirement(kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// Builds an asset packet OP_RETURN TxOut if any inputs or outputs carry assets.
    /// Groups assets by AssetId, computes input/output mappings, and assigns change to the last output.
    /// </summary>
    private static TxOut? BuildAssetPacket(IReadOnlyCollection<ArkCoin> inputs, ArkTxOut[] outputs)
    {
        // Collect asset inputs: map (assetId -> list of (inputIndex, amount))
        var assetInputs = new Dictionary<string, List<(ushort vin, ulong amount)>>();
        var inputList = inputs.ToList();
        for (var i = 0; i < inputList.Count; i++)
        {
            if (inputList[i].Assets is not { Count: > 0 } assets) continue;
            foreach (var asset in assets)
            {
                if (!assetInputs.ContainsKey(asset.AssetId))
                    assetInputs[asset.AssetId] = [];
                assetInputs[asset.AssetId].Add(((ushort)i, asset.Amount));
            }
        }

        // Collect asset outputs: map (assetId -> list of (outputIndex, amount))
        var assetOutputs = new Dictionary<string, List<(ushort vout, ulong amount)>>();
        for (var i = 0; i < outputs.Length; i++)
        {
            if (outputs[i].Assets is not { Count: > 0 } assets) continue;
            foreach (var asset in assets)
            {
                if (!assetOutputs.ContainsKey(asset.AssetId))
                    assetOutputs[asset.AssetId] = [];
                assetOutputs[asset.AssetId].Add(((ushort)i, asset.Amount));
            }
        }

        var allAssetIds = new HashSet<string>(assetInputs.Keys);
        allAssetIds.UnionWith(assetOutputs.Keys);

        if (allAssetIds.Count == 0)
            return null;

        // Find the change output index (last output, which is where BTC change goes)
        var changeOutputIndex = (ushort)(outputs.Length - 1);

        var groups = new List<AssetGroup>();
        foreach (var assetIdStr in allAssetIds)
        {
            var assetId = AssetId.FromString(assetIdStr);
            var groupInputs = assetInputs.GetValueOrDefault(assetIdStr)?
                .Select(x => AssetInput.Create(x.vin, x.amount))
                .ToList() ?? [];

            var groupOutputs = assetOutputs.GetValueOrDefault(assetIdStr)?
                .Select(x => AssetOutput.Create(x.vout, x.amount))
                .ToList() ?? [];

            // Compute asset change and assign to change output
            var totalIn = assetInputs.GetValueOrDefault(assetIdStr)?
                .Sum(x => (long)x.amount) ?? 0;
            var totalOut = assetOutputs.GetValueOrDefault(assetIdStr)?
                .Sum(x => (long)x.amount) ?? 0;
            var assetChange = totalIn - totalOut;
            if (assetChange > 0)
            {
                groupOutputs.Add(AssetOutput.Create(changeOutputIndex, (ulong)assetChange));
            }

            groups.Add(AssetGroup.Create(assetId, null, groupInputs, groupOutputs, []));
        }

        var packet = Packet.Create(groups);
        return packet.ToTxOut();
    }
}