using NArk.Abstractions;
using NArk.Contracts;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;

namespace NArk.Swaps.Boltz.Models;

public record SubmarineSwapResult(VHTLCContract Contract, SubmarineResponse Swap, ArkAddress Address);