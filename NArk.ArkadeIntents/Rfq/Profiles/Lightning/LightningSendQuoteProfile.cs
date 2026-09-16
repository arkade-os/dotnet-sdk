namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>
/// Quote fields of the Lightning send profile — compare-only, never trusted. The values a client
/// may act on are the quote's own binding fields.
/// </summary>
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
}
