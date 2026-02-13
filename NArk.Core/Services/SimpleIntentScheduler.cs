using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Models.Options;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Services;

public class SimpleIntentScheduler(IFeeEstimator feeEstimator, IClientTransport clientTransport, IContractService contractService, IChainTimeProvider chainTimeProvider, IOptions<SimpleIntentSchedulerOptions> options, ILogger<SimpleIntentScheduler>? logger = null) : IIntentScheduler
{
    public async Task<IReadOnlyCollection<ArkIntentSpec>> GetIntentsToSubmit(
        IReadOnlyCollection<ArkCoin> unspentVtxos, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Getting intents to submit for {VtxoCount} unspent vtxos", unspentVtxos.Count);
        ArgumentNullException.ThrowIfNull(chainTimeProvider);
        if (options.Value.ThresholdHeight is null && options.Value.Threshold is null)
        {
            logger?.LogError("SimpleIntentScheduler misconfigured: either thresholdHeight or threshold is required");
            throw new ArgumentNullException("Either thresholdHeight or threshold is required");
        }

        if (unspentVtxos.Count == 0)
        {
            logger?.LogDebug("No unspent vtxos to process");
            return [];
        }

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var chainTime = await chainTimeProvider.GetChainTime(cancellationToken);

        var coins = unspentVtxos
            .Where(v =>
                v.IsRecoverable(chainTime)
                 ||
                    (v.ExpiresAt is { } exp && options.Value.Threshold is { } thresh && exp - thresh < chainTime.Timestamp) ||
                    (v.ExpiresAtHeight is { } height && options.Value.ThresholdHeight is { } threshHeight && height - threshHeight < chainTime.Height)
            )
            .GroupBy(v => v.WalletIdentifier);

        List<ArkIntentSpec> intentSpecs = [];

        foreach (var g in coins)
        {
            //TODO: we are reserving many addresses this way needlessly, prob need use a last address here or unreserve somehow?
            // var outputContract = await contractService.DeriveContract(g.Key,NextContractPurpose.SendToSelf, cancellationToken);

            var inputsSumAfterBeforeFees = g.Sum(c => c.Amount);
            if (inputsSumAfterBeforeFees < serverInfo.Dust)
            {
                logger?.LogWarning("Wallet {WalletId} has inputs below dust threshold - skipping until quota above dust", g.Key);
                continue;
            }
            var specBeforeFees =
                new ArkIntentSpec(
                    g.ToArray(),
                    [
                        // new ArkTxOut(
                        //     ArkTxOutType.Vtxo,
                        //     inputsSumAfterBeforeFees,
                        //     outputContract.GetArkAddress()
                        // )
                    ],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddHours(1)
                );

            var fees = await feeEstimator.EstimateFeeAsync(specBeforeFees, cancellationToken);

            var inputsSumAfterAfterFees = inputsSumAfterBeforeFees - fees;

            if (inputsSumAfterAfterFees < Money.Zero)
            {
                logger?.LogDebug("Skipping wallet {WalletId}: inputs sum after fees is negative", g.Key);
                continue;
            }
            
            var inputContracts = g.Select(c => c.Contract).ToArray();
            var outputContract = await contractService.DeriveContract(g.Key, NextContractPurpose.SendToSelf, inputContracts, ContractActivityState.Inactive, cancellationToken: cancellationToken);
            var finalSpec =
                new ArkIntentSpec(
                    g.ToArray(),
                    [
                        new ArkTxOut(
                            ArkTxOutType.Vtxo,
                            inputsSumAfterAfterFees,
                            outputContract.GetArkAddress()
                        )
                    ],
                    null,
                    null
                );

            intentSpecs.Add(finalSpec);
            logger?.LogDebug("Created intent spec for wallet {WalletId} with {CoinCount} coins", g.Key, g.Count());
        }

        logger?.LogDebug("Generated {IntentSpecCount} intent specs", intentSpecs.Count);
        return intentSpecs;

    }
}