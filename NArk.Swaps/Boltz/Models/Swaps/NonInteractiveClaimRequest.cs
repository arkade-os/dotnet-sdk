using System.Text.Json.Serialization;

namespace NArk.Swaps.Boltz.Models.Swaps;

/// <summary>
/// Asks Boltz to add a non-interactive claim leaf to the VHTLC it builds, so a claim
/// daemon can sweep the swap while this wallet is offline.
/// </summary>
/// <remarks>
/// <para>
/// Only meaningful when Boltz is the one sending Arkade — reverse swaps and chain
/// swaps <em>to</em> ARK. Boltz rejects it elsewhere, and rejects it entirely unless
/// the server was started with non-interactive claims enabled.
/// </para>
/// <para>
/// Boltz never learns which claim daemon will act: it takes only the destination and
/// bakes it into the leaf. The co-signer key that makes the leaf spendable is chosen
/// by us and must belong to the daemon we later register with, which is why the leaf
/// has to be reproduced locally rather than trusted from the response.
/// </para>
/// </remarks>
public class NonInteractiveClaimRequest
{
    /// <summary>
    /// bech32m Arkade address the claim must pay. Must be on the same network and
    /// server as the swap, and should be an address this wallet already watches.
    /// </summary>
    [JsonPropertyName("claimAddress")]
    public required string ClaimAddress { get; set; }
}
