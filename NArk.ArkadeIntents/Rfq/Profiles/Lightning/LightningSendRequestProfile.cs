namespace NArk.ArkadeIntents.Rfq.Profiles.Lightning;

/// <summary>Request fields of the Lightning send profile.</summary>
public sealed class LightningSendRequestProfile
{
    /// <summary>The BOLT11 to pay. Its amount is authoritative, which is what forces exact-out.</summary>
    public required string Invoice { get; init; }

    /// <summary>The client's own Arkade address, where a failed swap refunds itself by covenant.</summary>
    public required string RefundAddress { get; init; }
}
