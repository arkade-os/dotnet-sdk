using NArk.Core.Contracts;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NBitcoin;

namespace NArk.Swaps.Boltz.Models;

/// <summary>
/// Result from creating a chain swap (BTC→ARK or ARK→BTC).
/// </summary>
public record ChainSwapResult(
    /// <summary>
    /// The Ark VHTLC contract (claim side for BTC→ARK, lockup side for ARK→BTC).
    /// </summary>
    VHTLCContract Contract,

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
    Key EphemeralBtcKey,

    /// <summary>
    /// TaprootSpendInfo for the BTC HTLC (reconstructed from Boltz's swap tree).
    /// Null when the BTC side has no swap tree (e.g. when we don't interact with BTC HTLC).
    /// </summary>
    TaprootSpendInfo? BtcHtlcSpendInfo);
