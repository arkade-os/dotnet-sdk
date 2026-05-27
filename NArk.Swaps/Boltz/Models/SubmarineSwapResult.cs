using NArk.Abstractions;
using NArk.Core.Contracts;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;

namespace NArk.Swaps.Boltz.Models;

/// <summary>
/// Result of creating an Arkade submarine swap (Arkade → Lightning) via Boltz.
/// </summary>
/// <param name="Contract">VHTLC contract that the sender must fund.</param>
/// <param name="Swap">Raw response from the Boltz API.</param>
/// <param name="Address">Arkade lockup address derived from the VHTLC contract.</param>
/// <param name="Invoice">
/// The Lightning invoice string to be paid by Boltz once the VHTLC is funded.
/// For BOLT 11 swaps this echoes the invoice supplied by the caller.
/// For BOLT 12 swaps this is the invoice fetched from the offer by Boltz.
/// <c>null</c> when not applicable (e.g. older call sites that predate BOLT 12 support).
/// </param>
public record SubmarineSwapResult(
    VHTLCContract Contract,
    SubmarineResponse Swap,
    ArkAddress Address,
    string? Invoice = null);