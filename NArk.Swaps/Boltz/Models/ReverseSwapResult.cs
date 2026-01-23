using NArk.Core.Contracts;
using NArk.Swaps.Boltz.Models.Swaps.Reverse;

namespace NArk.Swaps.Boltz.Models;

public record ReverseSwapResult(VHTLCContract Contract, ReverseResponse Swap, byte[] Hash);