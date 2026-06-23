using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Core.Assets;
using NArk.Core.Fees;
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

        var maxTxWeightWu = serverInfo.MaxTxWeight;

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
                g =>
                {
                    var ordered = g.OrderByDescending(v => v.Amount).ToList();
                    return ChunkByProofTxWeight(ordered, maxTxWeightWu);
                }
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
                var specBeforeFees = new ArkIntentSpec(
                    chunk,[], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)
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
    
    private static List<ArkCoin[]> ChunkByProofTxWeight(IReadOnlyList<ArkCoin> coins, long maxTxWeightWu)
    {
        // Overhead that every chunk pays regardless of coin count.
        const int fixedWu = ArkTxWeightEstimator.BaseTxWu + ArkTxWeightEstimator.P2TrOutputWu;

        var chunks = new List<ArkCoin[]>();
        var current = new List<ArkCoin>();
        var currentInputsWu = 0;
        var currentToSpendWu = 0; // WU of inputs[0]; appears twice in proof tx (regular input + toSpend duplicate)
        var currentAssetEntries = new List<(string assetId, ushort vin, ulong amount)>();
        var currentAssetPacketWu = 0;

        foreach (var coin in coins)
        {
            var coinWu = ArkTxWeightEstimator.GetInputWeightUnits(coin);
            var coinAssetEntries = GetAssetEntries(coin, (ushort)current.Count);

            // Asset OP_RETURN grows with each asset entry, so rebuild it when this coin carries assets.
            // For BTC-only wallets this branch is never taken and assetPacketWu stays 0.
            int tentativeAssetPacketWu;
            if (coinAssetEntries.Count > 0)
            {
                var allEntries = new List<(string assetId, ushort vin, ulong amount)>(currentAssetEntries.Count + coinAssetEntries.Count);
                allEntries.AddRange(currentAssetEntries);
                allEntries.AddRange(coinAssetEntries);
                var packet = AssetPacketBuilder.Build(allEntries, null, changeVout: 0);
                tentativeAssetPacketWu = packet is null ? 0 : ArkTxWeightEstimator.GetOutputWeightUnits(packet);
            }
            else
            {
                tentativeAssetPacketWu = currentAssetPacketWu;
            }

            // When the chunk is empty this coin will become inputs[0], so its WU counts twice (toSpend + regular).
            var toSpendWu = current.Count == 0 ? coinWu : currentToSpendWu;
            var tentativeWu = fixedWu + toSpendWu + currentInputsWu + coinWu + tentativeAssetPacketWu;

            if (tentativeWu > maxTxWeightWu && current.Count > 0)
            {
                // Coin doesn't fit — flush current chunk. The `current.Count > 0` guard ensures
                // a single oversized coin still gets emitted as its own chunk rather than looping.
                chunks.Add(current.ToArray());
                current = [coin];
                currentInputsWu = coinWu;
                currentToSpendWu = coinWu;
                currentAssetEntries = GetAssetEntries(coin, 0);
                currentAssetPacketWu = AssetPacketWu(currentAssetEntries);
            }
            else
            {
                if (current.Count == 0) currentToSpendWu = coinWu;
                current.Add(coin);
                currentInputsWu += coinWu;
                currentAssetEntries.AddRange(coinAssetEntries);
                currentAssetPacketWu = tentativeAssetPacketWu;
            }
        }

        if (current.Count > 0)
        {
            chunks.Add(current.ToArray());
        }

        return chunks;
    }

    private static List<(string assetId, ushort vin, ulong amount)> GetAssetEntries(ArkCoin coin, ushort vin)
    {
        if (coin.Assets is not { Count: > 0 } assets)
        {
            return [];
        }
        return [..assets.Select(a => (a.AssetId, vin, a.Amount))];
    }

    private static int AssetPacketWu(List<(string assetId, ushort vin, ulong amount)> entries)
    {
        if (entries.Count == 0)
        {
            return 0;
        }
        var packet = AssetPacketBuilder.Build(entries, null, changeVout: 0);
        return packet is null ? 0 : ArkTxWeightEstimator.GetOutputWeightUnits(packet);
    }
}
