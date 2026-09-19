namespace NArk.ArkadeIntents.Rfq.Profiles.Asset;

/// <summary>
/// Request fields of the <c>arkade:X-&gt;arkade:Y</c> profile: the client's own position in the
/// offer covenant.
/// </summary>
/// <remarks>
/// Both fields are covenant parameters rather than preferences, which is why the solver's schema is
/// strict about them: the offer it quotes back is derived from exactly these two values, so a
/// misspelling that was quoted anyway would have the client funding a script derived from something
/// the solver never read. That failure has no error — it is a deposit sitting at an address nothing
/// is watching.
/// </remarks>
public sealed class ArkadeSwapRequestProfile
{
    /// <summary>
    /// The client's own taproot scriptPubKey, hex — 34 bytes, <c>OP_1 &lt;32-byte program&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Where the fill must pay. The covenant takes the witness program (this value minus its 2-byte
    /// prefix), so a script of any other length would be sliced into a program of the wrong size and
    /// the covenant would oblige a payment to an output nobody controls.
    /// </remarks>
    public required string MakerPkScript { get; init; }

    /// <summary>The client's x-only key, hex — the <c>cancel</c> path's signer.</summary>
    public required string MakerPublicKey { get; init; }
}
