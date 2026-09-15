namespace NArk.ArkadeIntents.Rfq.Profiles.Onchain;

/// <summary>
/// Quote fields of the onchain send profile — compare-only, never trusted.
/// </summary>
/// <remarks>
/// This corridor has two contracts, one per rail, and the quote describes both. Neither is believed:
/// each is rebuilt locally and the solver's rendering of it is only ever compared against ours.
/// </remarks>
public sealed class OnchainSendQuoteProfile
{
    /// <summary>
    /// The solver's derivation of the Arkade covenant's address. Compare-only — check it against
    /// your own and refuse to fund on any mismatch.
    /// </summary>
    public string? LockupAddress { get; init; }

    /// <summary>
    /// The solver's own claim destination as a P2TR scriptPubKey (hex), pinned by the Arkade
    /// covenant's <c>nonInteractiveClaim</c> leaf.
    /// </summary>
    /// <remarks>
    /// Compare-only, but also an input: every leaf feeds the merkle root, so the local
    /// reconstruction needs this exact value to reach a matching address. A wrong one costs the
    /// solver a spending path and the client nothing.
    /// </remarks>
    public string? ReceiverPkScript { get; init; }

    /// <summary>The solver's x-only key (hex) that reclaims the L1 HTLC once its locktime matures.</summary>
    public string? HtlcPubkey { get; init; }

    /// <summary>The L1 HTLC's absolute refund locktime, unix seconds.</summary>
    /// <remarks>
    /// Must mature <em>before</em> the Arkade covenant's own refund, with margin. The two deadlines
    /// in the wrong order is the one failure that can cost both legs at once — see
    /// <c>OnchainSendGates</c>.
    /// </remarks>
    public long? HtlcLocktime { get; init; }

    /// <summary>The solver's derivation of the L1 HTLC address. Compare-only.</summary>
    public string? HtlcAddress { get; init; }

    /// <summary>How many confirmations the solver's L1 funding needs before it is safe to claim.</summary>
    public int? MinConfirmations { get; init; }
}
