using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NBitcoin;

namespace NArk.Swaps.Boltz.Models;

/// <summary>
/// Result from creating a chain swap (BTC→ARK or ARK→BTC).
/// Boltz's fulmine sidecar creates the Ark VHTLC — we only need the lockup addresses.
/// The BTC Taproot HTLC spend info is reconstructed at claim time from the stored response.
/// </summary>
public record ChainSwapResult(
    /// <summary>
    /// The Boltz chain swap response with both sides' details.
    /// </summary>
    ChainResponse Swap,

    /// <summary>
    /// The preimage (32 bytes) — needed for claiming.
    /// </summary>
    byte[] Preimage,

    /// <summary>
    /// SHA256 hash of the preimage — the payment hash used by Boltz.
    /// </summary>
    byte[] PreimageHash,

    /// <summary>
    /// Ephemeral BTC key for MuSig2 operations.
    /// </summary>
    Key EphemeralBtcKey);
