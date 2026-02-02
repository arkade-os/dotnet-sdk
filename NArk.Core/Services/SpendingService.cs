using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;

using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Enums;
using NArk.Core.Events;
using NArk.Core.Helpers;
using NArk.Core.Transport;
using NArk.Core.Extensions;
using NBitcoin;
using CoinSelector_ICoinSelector = NArk.Core.CoinSelector.ICoinSelector;
using ICoinSelector = NArk.Core.CoinSelector.ICoinSelector;

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
        : this(vtxoStorage, contractStorage, coinService, walletProvider, paymentService, transport, coinSelector, safetyService, intentStorage, [], null)
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
        : this(vtxoStorage, contractStorage, coinService, walletProvider, paymentService, transport, coinSelector, safetyService, intentStorage, [], logger)
    {
    }

    public async Task<uint256> Spend(string walletId, ArkCoin[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Spending {InputCount} inputs with {OutputCount} outputs for wallet {WalletId}", inputs.Length, outputs.Length, walletId);
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
                // GetDestination uses DerivePaymentContract, which saves the contract to DB
                changeAddress = (await paymentService.DeriveContract(walletId, NextContractPurpose.SendToSelf, cancellationToken: cancellationToken)).GetArkAddress();
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

            var transactionBuilder = new TransactionHelpers.ArkTransactionBuilder(transport, safetyService, walletProvider, intentStorage);

            var txId = await transactionBuilder.ConstructAndSubmitArkTransaction(inputs, outputs, cancellationToken);

            logger?.LogInformation("Spend transaction {TxId} completed successfully for wallet {WalletId}", txId, walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(new PostCoinsSpendActionEvent([.. inputs], txId,
                ActionState.Successful, null), cancellationToken: cancellationToken);

            return txId;
        }
        catch (Exception ex)
        {
            logger?.LogError(0, ex, "Spend transaction failed for wallet {WalletId}", walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(new PostCoinsSpendActionEvent([.. inputs], null,
                ActionState.Failed, $"Spending coins failed with ex: {ex}"), cancellationToken: cancellationToken);

            throw;
        }
    }

    public async Task<IReadOnlySet<ArkCoin>> GetAvailableCoins(string walletId, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Getting available coins for wallet {WalletId}", walletId);
        
        var contractByScript =
            (await contractStorage.GetContracts(walletIds: [walletId], cancellationToken: cancellationToken))
                .ToDictionary(entity => entity.Script);
        
        var vtxos = await vtxoStorage.GetVtxos(scripts: contractByScript.Keys.ToList(), walletIds: [walletId], includeSpent:false,  cancellationToken: cancellationToken);
        
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
                    logger?.LogDebug(0, ex, "Skipping vtxo {TxId}:{Index} - requires additional information (likely VHTLC contract)", vtxo.TransactionId, vtxo.TransactionOutputIndex);
                }
            }
        }

        logger?.LogDebug("Found {CoinCount} available coins for wallet {WalletId}", coins.Count, walletId);
        return coins;
    }

    public async Task<uint256> Spend(string walletId, ArkTxOut[] outputs, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Spending with automatic coin selection for wallet {WalletId} with {OutputCount} outputs", walletId, outputs.Length);
        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

        var outputsSumInSatoshis = outputs.Sum(o => o.Value);

        // Check if any output is explicitly subdust (the user wants to send subdust amount)
        var hasExplicitSubdustOutput = outputs.Count(o => o.Value < serverInfo.Dust);
        
        var coins = await GetAvailableCoins(walletId, cancellationToken);
        var selectedCoins = coinSelector.SelectCoins([.. coins], outputsSumInSatoshis, serverInfo.Dust,
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
                // GetDestination uses DerivePaymentContract, which saves the contract to DB
                changeAddress = (await paymentService.DeriveContract(walletId, NextContractPurpose.SendToSelf, cancellationToken: cancellationToken)).GetArkAddress();
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

            var transactionBuilder = new TransactionHelpers.ArkTransactionBuilder(transport, safetyService, walletProvider, intentStorage);

            var txId = await transactionBuilder.ConstructAndSubmitArkTransaction(selectedCoins, outputs, cancellationToken);

            logger?.LogInformation("Spend transaction {TxId} completed successfully for wallet {WalletId} with automatic coin selection", txId, walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(new PostCoinsSpendActionEvent(coins.ToArray(), txId,
                ActionState.Successful, null), cancellationToken: cancellationToken);

            return txId;
        }
        catch (Exception ex)
        {
            logger?.LogError(0, ex, "Spend transaction with automatic coin selection failed for wallet {WalletId}", walletId);
            await postSpendEventHandlers.SafeHandleEventAsync(new PostCoinsSpendActionEvent(coins.ToArray(), null,
                ActionState.Failed, $"Spending selected coins failed with ex: {ex}"), cancellationToken: cancellationToken);

            throw;
        }
    }
}