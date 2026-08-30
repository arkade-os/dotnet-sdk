using Microsoft.Extensions.Logging;
using NArk.Core.Models.Options;
using NBitcoin;

namespace NArk.Core.Batches;

/// <summary>
/// Bounds the batch expiry an Arkade server declares when it opens a batch, before the client
/// commits to it.
/// </summary>
/// <remarks>
/// The expiry becomes the timelock of the sweep leaf — the operator's only unilateral path out of the
/// batch output. Tree validation cannot vouch for it, because the sweep root it checks against is
/// derived from that same expiry, so this policy is the only check on the value. See the
/// "Batch Expiry Validation" article for the attack it prevents.
/// </remarks>
public sealed record BatchExpiryPolicy
{
    /// <summary>BIP-68 encodes seconds-typed relative timelocks in units of 512 seconds.</summary>
    public const int SecondsGranularity = 512;

    /// <summary>Largest seconds-typed value BIP-68 can encode (65535 x 512, roughly 388 days).</summary>
    private const long MaxEncodableSeconds = 0xFFFFL * SecondsGranularity;

    private static readonly TimeSpan DefaultMinimumExpiry = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultRegtestMinimumExpiry = TimeSpan.FromSeconds(SecondsGranularity);
    private const int DefaultMinimumExpiryBlocks = 144;
    private const int DefaultRegtestMinimumExpiryBlocks = 10;

    /// <summary>
    /// Builds a policy.
    /// </summary>
    /// <param name="allowBlockTypedExpiry">Whether a block-typed expiry is acceptable.</param>
    /// <param name="minimumExpiry">
    /// Shortest acceptable seconds-typed expiry. Must be at least one BIP-68 granularity unit
    /// (<see cref="SecondsGranularity"/> seconds).
    /// </param>
    /// <param name="minimumExpiryBlocks">Shortest acceptable block-typed expiry, in blocks.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minimumExpiryBlocks"/> is zero or negative, or <paramref name="minimumExpiry"/>
    /// is below one granularity unit. The floors can be lowered but not disabled.
    /// </exception>
    public BatchExpiryPolicy(bool allowBlockTypedExpiry, TimeSpan minimumExpiry, int minimumExpiryBlocks)
    {
        // Validate compares against the floor rounded down to a multiple of SecondsGranularity, so a
        // floor below one unit rounds to zero and accepts every seconds-typed expiry — including the
        // 512s minimum the encoding allows. Such a floor reads as lowered but behaves as switched
        // off, so it is rejected here rather than silently disabling the check.
        if (minimumExpiry < TimeSpan.FromSeconds(SecondsGranularity))
            throw new ArgumentOutOfRangeException(nameof(minimumExpiry), minimumExpiry,
                $"Minimum batch expiry must be at least {SecondsGranularity} seconds, the BIP-68 granularity " +
                "unit the floor is rounded down to. A shorter floor rounds down to zero and would accept " +
                "any expiry. The floor can be lowered but not disabled.");
        if (minimumExpiryBlocks <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumExpiryBlocks), minimumExpiryBlocks,
                "Minimum batch expiry in blocks must be greater than zero. The floor can be lowered but not disabled.");

        AllowBlockTypedExpiry = allowBlockTypedExpiry;
        MinimumExpiry = minimumExpiry;
        MinimumExpiryBlocks = minimumExpiryBlocks;
    }

    /// <summary>Whether a block-typed expiry is acceptable. Only regtest allows one by default.</summary>
    public bool AllowBlockTypedExpiry { get; }

    /// <summary>Shortest acceptable seconds-typed expiry.</summary>
    public TimeSpan MinimumExpiry { get; }

    /// <summary>Shortest acceptable block-typed expiry, in blocks.</summary>
    public int MinimumExpiryBlocks { get; }

    /// <summary>
    /// Resolves the policy for a network, applying any caller overrides on top of the defaults.
    /// </summary>
    /// <param name="network">The network the Arkade server advertises.</param>
    /// <param name="options">Optional overrides; <c>null</c> properties keep the default.</param>
    /// <returns>The policy to enforce.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An override would disable rather than lower a floor. See the constructor for the bounds.
    /// </exception>
    /// <remarks>
    /// Regtest allows a block-typed expiry with a 10-block floor, mirroring arkd, whose block-typed
    /// VTXO tree expiry is regtest-only; every other network requires a seconds-typed expiry of at
    /// least 24 hours. A server advertising "mutinynet" resolves to <see cref="Network.TestNet"/>, so
    /// it gets the strict policy.
    /// </remarks>
    public static BatchExpiryPolicy ForNetwork(Network network, BatchExpiryOptions? options = null)
    {
        var regtest = network == Network.RegTest;

        return new BatchExpiryPolicy(
            options?.AllowBlockTypedExpiry ?? regtest,
            options?.MinimumExpiry ?? (regtest ? DefaultRegtestMinimumExpiry : DefaultMinimumExpiry),
            options?.MinimumExpiryBlocks ?? (regtest ? DefaultRegtestMinimumExpiryBlocks : DefaultMinimumExpiryBlocks));
    }

    /// <summary>
    /// Applies the BIP-68 encoding the Arkade protocol uses for batch expiries: values below 512 are
    /// a block count, everything else is a number of seconds rounded down to a multiple of 512.
    /// </summary>
    /// <param name="declaredExpiry">The expiry exactly as the server declared it.</param>
    /// <returns>The encoded sequence.</returns>
    /// <exception cref="InvalidBatchExpiryException">The value cannot be encoded as a relative timelock.</exception>
    public static Sequence Encode(long declaredExpiry)
    {
        if (declaredExpiry <= 0)
            throw new InvalidBatchExpiryException(
                $"Arkade server declared a batch expiry of {declaredExpiry}, which is not a positive relative timelock.");

        if (declaredExpiry < SecondsGranularity)
            return new Sequence((int)declaredExpiry);

        if (declaredExpiry > MaxEncodableSeconds)
            throw new InvalidBatchExpiryException(
                $"Arkade server declared a batch expiry of {declaredExpiry} seconds, which exceeds the largest " +
                $"BIP-68 encodable relative timelock ({MaxEncodableSeconds} seconds).");

        return new Sequence(TimeSpan.FromSeconds(declaredExpiry));
    }

    /// <summary>
    /// Validates a declared batch expiry and returns the sequence to commit to the sweep leaf.
    /// </summary>
    /// <param name="declaredExpiry">
    /// The expiry exactly as the server declared it — <see cref="Abstractions.Batches.ServerEvents.BatchStartedEvent.RawBatchExpiry"/>.
    /// </param>
    /// <param name="logger">Optional logger; receives a warning when the declared value is not encodable exactly.</param>
    /// <returns>The encoded sequence, which is what the sweep leaf actually commits to.</returns>
    /// <exception cref="InvalidBatchExpiryException">
    /// The expiry is not encodable, is block-typed where the policy forbids it, or falls below the floor.
    /// </exception>
    public Sequence Validate(long declaredExpiry, ILogger? logger = null)
    {
        var encoded = Encode(declaredExpiry);

        if (encoded.LockType == SequenceLockType.Height)
        {
            if (!AllowBlockTypedExpiry)
                throw new InvalidBatchExpiryException(
                    $"Arkade server declared a block-typed batch expiry of {declaredExpiry} blocks. Only a " +
                    "seconds-typed expiry is accepted on this network, because a block-typed one bounds the " +
                    "operator's sweep in blocks rather than wall-clock time.");

            if (encoded.LockHeight < MinimumExpiryBlocks)
                throw new InvalidBatchExpiryException(
                    $"Arkade server declared a batch expiry of {encoded.LockHeight} blocks, below the minimum of " +
                    $"{MinimumExpiryBlocks} blocks. The operator could sweep the batch output before a unilateral " +
                    "exit could complete.");

            return encoded;
        }

        // BIP-68 cannot express every second, so hold the floor to the same granularity as the values
        // it is compared against — otherwise a 24h floor would reject 86016, the closest encodable
        // value below 24h, for being 384 seconds short.
        var floorSeconds = (long)MinimumExpiry.TotalSeconds / SecondsGranularity * SecondsGranularity;
        var encodedSeconds = (long)encoded.LockPeriod.TotalSeconds;

        if (encodedSeconds < floorSeconds)
            throw new InvalidBatchExpiryException(
                $"Arkade server declared a batch expiry of {declaredExpiry} seconds (encoded as {encodedSeconds}), " +
                $"below the minimum of {floorSeconds} seconds. The operator could sweep the batch output before a " +
                "unilateral exit could complete.");

        if (declaredExpiry % SecondsGranularity != 0)
            logger?.LogWarning(
                "Arkade server declared a batch expiry of {DeclaredExpiry}s, which BIP-68 cannot encode exactly; " +
                "the sweep leaf commits to {EncodedExpiry}s. If the server rounds differently, batch tree " +
                "validation will reject the batch.",
                declaredExpiry, encodedSeconds);

        return encoded;
    }
}
