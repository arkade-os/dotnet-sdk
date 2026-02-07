using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NBitcoin;
using ICoinSelector = NArk.Core.CoinSelector.ICoinSelector;

namespace NArk.Core.Services;

public class OnchainService(IClientTransport clientTransport, IContractService contractService, ISpendingService spendingService, IIntentGenerationService intentGenerationService, IFeeEstimator feeEstimator, ICoinSelector coinSelector, ILogger<OnchainService>? logger = null) : IOnchainService
{
    public async Task<string> InitiateCollaborativeExit(string walletId, ArkTxOut output,
        CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Initiating collaborative exit for wallet {WalletId} with output value {Value}", walletId, output.Value);
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);

        if (output.Value < serverInfo.Dust)
        {
            logger?.LogWarning("Collaborative exit rejected for wallet {WalletId}: output value {Value} is below dust threshold {Dust}", walletId, output.Value, serverInfo.Dust);
            throw new InvalidOperationException("Output value is below dust threshold.");
        }

        var availableCoins =
            await spendingService.GetAvailableCoins(walletId, cancellationToken);

        var changeAddress = (await contractService.DeriveContract(walletId, NextContractPurpose.SendToSelf, cancellationToken: cancellationToken)).GetArkAddress();

        var outputValueUsedForCoinSelection = output.Value;

        while (true)
        {
            var selectedCoins = coinSelector.SelectCoins([.. availableCoins], outputValueUsedForCoinSelection, serverInfo.Dust, 0);

            var totalInput = selectedCoins.Sum(x => x.TxOut.Value);
            var change = totalInput - output.Value;

            var estimatedFeeIfChange = await feeEstimator.EstimateFeeAsync(
                [.. selectedCoins],
                [
                    output,
                    new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(change), changeAddress!)
                ],
                cancellationToken);

            if (change >= estimatedFeeIfChange + serverInfo.Dust)
            {
                ArkTxOut[] outputs = [
                    output,
                    new(ArkTxOutType.Vtxo, Money.Satoshis(change - estimatedFeeIfChange), changeAddress!)
                ];
                return await InitiateCollaborativeExit(selectedCoins.ToArray(), outputs, cancellationToken);
            }
            else
            {
                var estimatedFeeIfNoChange = await feeEstimator.EstimateFeeAsync(
                    [.. selectedCoins],
                    [output],
                    cancellationToken);

                if (totalInput >= output.Value + Math.Max(serverInfo.Dust, estimatedFeeIfNoChange))
                {
                    ArkTxOut[] outputs = [output];
                    return await InitiateCollaborativeExit(selectedCoins.ToArray(), outputs, cancellationToken);
                }

                // Increase output value to force selection of more coins
                outputValueUsedForCoinSelection += serverInfo.Dust;
            }
        }
    }

    public async Task<string> InitiateCollaborativeExit(ArkCoin[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Initiating collaborative exit with {InputCount} inputs and {OutputCount} outputs", inputs.Length, outputs.Length);
        if (outputs.All(o => o.Type == ArkTxOutType.Vtxo))
        {
            logger?.LogWarning("Collaborative exit rejected: no on-chain outputs provided");
            throw new InvalidOperationException("No on-chain outputs provided for collaborative exit.");
        }
        if (inputs.Select(i => i.WalletIdentifier).Distinct().Count() != 1)
        {
            logger?.LogWarning("Collaborative exit rejected: inputs belong to multiple wallets");
            throw new InvalidOperationException("All inputs must belong to the same wallet for collaborative exit.");
        }

        var intentSpec = new ArkIntentSpec(
            [.. inputs],
            outputs,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(1)
        );

        // Create intent (automatically cancels any overlapping intents)
        var intent =
            await intentGenerationService.GenerateManualIntent(inputs[0].WalletIdentifier, intentSpec, cancellationToken);

        logger?.LogInformation("Collaborative exit initiated for wallet {WalletId} with intent {IntentId}", inputs[0].WalletIdentifier, intent);
        return intent;
    }
}