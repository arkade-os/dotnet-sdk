using NArk.Core.Contracts;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NBitcoin;

namespace NArk.Swaps.Boltz.Models;

/// <summary>
/// Result from creating a chain swap (BTC→ARK or ARK→BTC).
/// For BTC→ARK, includes the VHTLC contract for claiming ARK VTXOs.
/// The BTC Taproot HTLC spend info is reconstructed at claim time from the stored response.
/// </summary>
public record ChainSwapResult(
    ChainResponse Swap,
    byte[] Preimage,
    byte[] PreimageHash,
    Key EphemeralBtcKey,
    // VHTLC contract for the ARK side (BTC→ARK only). Null for ARK→BTC.
    VHTLCContract? Contract = null);
