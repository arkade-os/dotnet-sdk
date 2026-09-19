namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>
/// Quote fields of the Lightning send profile: compare-only, except for the one rung of the CSV
/// ladder the solver is allowed to set.
/// </summary>
/// <remarks>
/// The rule this profile follows, and the exception to it, are both worth stating. Everything here
/// is checked against the client's own derivation rather than believed —
/// <see cref="LockupAddress"/> most of all. <see cref="RefundWithoutReceiverDelay"/> is the
/// exception: it is a binding field, built into the script the client funds, because it is the one
/// number the client cannot derive on its own.
/// </remarks>
public sealed class LightningSendQuoteProfile
{
    /// <summary>The invoice's payment hash, echoed back.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>
    /// The solver's derivation of the swap contract's address. Compare-only: check it against your
    /// own derivation and refuse to fund on any mismatch.
    /// </summary>
    public string? LockupAddress { get; init; }

    /// <summary>
    /// The solver's own claim destination as a P2TR scriptPubKey (hex), which the covenant's
    /// <c>nonInteractiveClaim</c> leaf pins its payout to.
    /// </summary>
    /// <remarks>
    /// Compare-only, but unlike <see cref="LockupAddress"/> it is also an <em>input</em>: every leaf
    /// contributes to the merkle root, so the local reconstruction needs this exact value to arrive
    /// at a matching address. It carries none of <see cref="LockupAddress"/>'s trust weight though —
    /// a wrong value only makes that one leaf unusable for the solver, and it pays the solver, never
    /// away from anything the client controls.
    /// </remarks>
    public string? ReceiverPkScript { get; init; }

    /// <summary>
    /// The exact BIP68 delay, in seconds, the client must put in its
    /// <c>unilateralRefundWithoutReceiver</c> leaf.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Binding</b>, unlike the rest of this profile: it goes into the script rather than being
    /// compared against one. The solver stretches this rung past the operator's base ladder whenever
    /// the horizon it just quoted needs it to, so a client re-deriving the base value alone builds a
    /// different covenant and ends up refusing a quote that was never wrong.
    /// </para>
    /// <para>
    /// Absent on a solver predating the field, hence nullable rather than required. See
    /// <see cref="NArk.ArkadeIntents.Lightning.LightningCorridor.ResolveSoloRefundDelay"/> for what
    /// happens then, and for every bound checked before this number reaches a leaf.
    /// </para>
    /// </remarks>
    public long? RefundWithoutReceiverDelay { get; init; }
}
