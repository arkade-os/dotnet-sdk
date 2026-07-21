using NBitcoin;

namespace NArk.Arkade.Program.Models;

/// <summary>
/// The Bitcoin-Script segment of a spending path — enforced on-chain. Mirrors the
/// ts-sdk's <c>TapscriptSegment</c>.
/// </summary>
/// <remarks>
/// At most one of <see cref="Asm"/>, <see cref="Csv"/>, <see cref="Cltv"/> may be set —
/// enforced later by validation, not by this data holder.
/// </remarks>
public sealed class TapscriptSegment
{
    /// <summary>Required signers. The tweaked co-signer key is appended automatically for covenant paths.</summary>
    public required IReadOnlyList<AsmToken> Signers { get; init; }

    /// <summary>Optional standard-opcode condition (e.g. a hashlock), encoded into a condition-multisig leaf.</summary>
    public IReadOnlyList<AsmToken>? Asm { get; init; }

    /// <summary>
    /// Relative timelock (CSV). Mutually exclusive with <see cref="Cltv"/>/<see cref="Asm"/> —
    /// enforced later by validation (mirroring the ts-sdk's <c>validateTapscript</c>,
    /// which runs at compile time, not here); <see cref="NBitcoin.Sequence"/> already
    /// distinguishes block-count vs. time-based (<see cref="SequenceLockType.Time"/>) locks.
    /// </summary>
    public Sequence? Csv { get; init; }

    /// <summary>Absolute timelock (CLTV). Mutually exclusive with <see cref="Csv"/>/<see cref="Asm"/>.</summary>
    public LockTime? Cltv { get; init; }

    /// <summary>Items satisfying the condition (e.g. an HTLC preimage).</summary>
    public IReadOnlyList<AsmToken>? Witness { get; init; }
}
