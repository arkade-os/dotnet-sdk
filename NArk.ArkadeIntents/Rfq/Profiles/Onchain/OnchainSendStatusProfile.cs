namespace NArk.ArkadeIntents.Rfq.Profiles.Onchain;

/// <summary>Status fields the solver reports for an onchain send swap.</summary>
/// <remarks>
/// Advisory throughout. What actually happened is read off the two chains — the Arkade covenant's
/// VTXO and the L1 HTLC's outputs — never from the party with an interest in the answer.
/// </remarks>
public sealed class OnchainSendStatusProfile
{
    /// <summary>The transaction that funded the L1 HTLC, if the solver has broadcast one.</summary>
    public string? HtlcTxid { get; init; }

    /// <summary>The preimage, once the solver has seen the client's L1 claim reveal it.</summary>
    public string? Preimage { get; init; }
}
