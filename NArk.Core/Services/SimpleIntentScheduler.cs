using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Core.Models.Options;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Services;

public class SimpleIntentScheduler(IFeeEstimator feeEstimator, IClientTransport clientTransport, IContractService contractService, IBitcoinBlockchain chainTimeProvider, IOptions<SimpleIntentSchedulerOptions> options, ILogger<SimpleIntentScheduler>? logger = null) : IIntentScheduler
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
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var coins = unspentVtxos
            .Where(v =>
                (
                    // Unrolled coins (boarding UTXOs, unrolled VTXOs) should be batched ASAP
                    // — they're sitting on-chain and we race against the exit delay expiry.
                    // Skip unconfirmed boarding UTXOs (no expiry yet) — arkd rejects unconfirmed inputs.
                    (v.Unrolled && v.ExpiresAt is not null) ||
                    v.IsRecoverable(chainTime) ||
                    (v.ExpiresAt is { } exp && options.Value.Threshold is { } thresh &&
                     exp - thresh < chainTime.Timestamp) ||
                    (v.ExpiresAtHeight is { } height && options.Value.ThresholdHeight is { } threshHeight &&
                     height - threshHeight < chainTime.Height)
                )
                // A coin under a deprecated signer past its cutoff that still requires a forfeit cannot
                // join a batch — the operator won't co-sign its forfeit (the old key is gone), so arkd
                // rejects the whole intent and bricks every other coin chunked with it. Hold it back
                // until it is forfeit-free (swept/unrolled), when it re-enrolls under the current signer.
                && !(v.RequiresForfeit() && v.IsDeprecatedSignerPastCutoff(serverInfo.DeprecatedSigners, nowUnix))
            )
            .GroupBy(v => v.WalletIdentifier)
            /*
             TODO(11.06.2026): maybe we could solve tail redistribution problem with remaining sub-dust buckets
             by swapping bigger vtxos from big buckets to sub-dust buckets 
            */
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(v => v.Amount).Chunk(ArkTransactionLimits.MaxVtxosPerArkTransaction).ToArray()
            );

        List<ArkIntentSpec> intentSpecs = [];

        foreach (var (walletId, chunks) in coins)
        {
            foreach (var chunk in chunks)
            {
                //TODO: we are reserving many addresses this way needlessly, prob need use a last address here or unreserve somehow?
                // var outputContract = await contractService.DeriveContract(walletId, NextContractPurpose.SendToSelf, cancellationToken);

                var inputsSumAfterBeforeFees = chunk.Sum(c => c.Amount);
                if (inputsSumAfterBeforeFees < serverInfo.Dust)
                {
                    logger?.LogWarning("Skipping a {CoinCount}-input chunk for wallet {WalletId}: chunk sum is below the dust threshold", chunk.Length, walletId);
                    continue;
                }
                var specBeforeFees =
                    new ArkIntentSpec(
                        chunk,
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
                    logger?.LogDebug("Skipping wallet {WalletId} chunk: inputs sum after fees is negative", walletId);
                    continue;
                }

                var inputContracts = chunk.Select(c => c.Contract).ToArray();
                var outputContract = await contractService.DeriveContract(walletId, NextContractPurpose.SendToSelf, inputContracts, ContractActivityState.Inactive, cancellationToken: cancellationToken);
                var finalSpec =
                    new ArkIntentSpec(
                        chunk,
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
                logger?.LogDebug("Created intent spec for wallet {WalletId} with {CoinCount} coins", walletId, chunk.Length);
            }
        }

        logger?.LogDebug("Generated {IntentSpecCount} intent specs", intentSpecs.Count);
        return intentSpecs;

    }
}