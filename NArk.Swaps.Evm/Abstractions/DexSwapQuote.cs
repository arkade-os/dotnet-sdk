using System.Numerics;
using NArk.Swaps.Evm.Contracts.Router;

namespace NArk.Swaps.Evm;

/// <summary>
/// The <see cref="Call"/> sequence Router's <c>executeCalls</c> should run to swap
/// <c>amountIn</c> of the input token into the output token (e.g. an <c>approve</c> to a DEX
/// router followed by the actual swap call), plus the minimum acceptable output for slippage
/// protection. Built by an <see cref="IDexQuoteProvider"/> — see that interface's doc comment
/// for why this is pluggable rather than hardcoded to one DEX.
/// </summary>
public record DexSwapQuote(IReadOnlyList<Call> Calls, BigInteger MinAmountOut);