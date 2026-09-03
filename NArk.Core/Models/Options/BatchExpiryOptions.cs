namespace NArk.Core.Models.Options;

/// <summary>
/// Overrides for the bounds applied to the batch expiry an Arkade server declares when it opens a
/// batch. Every property is optional; a <c>null</c> property keeps the network-specific default
/// described on <see cref="NArk.Core.Batches.BatchExpiryPolicy"/>.
/// </summary>
/// <remarks>
/// The floors can be lowered but not switched off. A non-null floor that would stop rejecting
/// anything is refused at startup rather than silently disabling the check: zero or less for either
/// floor, and additionally anything below 512 seconds for <see cref="MinimumExpiry"/>, which is
/// rounded down to a multiple of 512 before comparison and so would round to zero.
/// </remarks>
public class BatchExpiryOptions
{
    /// <summary>
    /// Shortest seconds-typed batch expiry this client will accept. Defaults to 24 hours, or
    /// 512 seconds on regtest.
    /// </summary>
    /// <remarks>
    /// BIP-68 encodes seconds in units of 512, so the floor is itself rounded down to a multiple of
    /// 512 before comparison. A 24-hour floor therefore accepts 86016 (168 x 512), the closest
    /// encodable value below a literal 86400. For the same reason it must be at least 512 seconds
    /// when set: a shorter floor rounds down to zero and would accept any declared expiry.
    /// </remarks>
    public TimeSpan? MinimumExpiry { get; set; }

    /// <summary>
    /// Shortest block-typed batch expiry this client will accept, in blocks. Only consulted when
    /// block-typed expiries are allowed at all. Defaults to 10 on regtest. Must be greater than
    /// zero when set.
    /// </summary>
    public int? MinimumExpiryBlocks { get; set; }

    /// <summary>
    /// Whether to accept a block-typed (rather than seconds-typed) batch expiry. Defaults to
    /// <c>true</c> on regtest and <c>false</c> everywhere else, mirroring arkd, which only permits
    /// a block-typed VTXO tree expiry on regtest.
    /// </summary>
    /// <remarks>
    /// Enabling this off regtest weakens the guarantee: block-typed expiries are bounded in blocks
    /// rather than wall-clock time, so their real duration depends on how fast blocks arrive. Only
    /// set it if you are talking to a server you know is configured that way.
    /// </remarks>
    public bool? AllowBlockTypedExpiry { get; set; }
}
