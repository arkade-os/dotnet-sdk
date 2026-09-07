namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>Request fields of the Lightning send profile.</summary>
public sealed class LightningSendRequestProfile
{
    /// <summary>The BOLT11 to pay. Its amount is authoritative, which is what forces exact-out.</summary>
    public required string Invoice { get; init; }

    /// <summary>The client's own Arkade address, where a failed swap refunds itself by covenant.</summary>
    public required string RefundAddress { get; init; }

    /// <summary>
    /// The client's own x-only key (32 bytes, hex), which the covenant's three client-side refund
    /// leaves are built around.
    /// </summary>
    /// <remarks>
    /// Required: the solver's schema is strict, so a request without it is refused outright rather
    /// than quoted on the older key-less script. Losing the matching private key forfeits the one
    /// recourse that needs nobody — see <c>refundUnilateral</c>, spendable by this key alone once
    /// its CSV delay has passed, whether or not the Arkade server and the emulator are reachable.
    /// </remarks>
    public required string ClientRefundPubkey { get; init; }
}
