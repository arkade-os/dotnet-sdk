using System.Numerics;
using Nethereum.Hex.HexConvertors.Extensions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Evm.Contracts.Router;

namespace NArk.Swaps.Evm.Dex;

/// <summary>
/// <see cref="IDexQuoteProvider"/> backed by Boltz's own <c>/v2/quote/{currency}/in</c> and
/// <c>/v2/quote/{currency}/encode</c> endpoints.
/// </summary>
/// <remarks>
/// Boltz's reference TS SDK (<c>boltz-swaps</c>'s <c>quoteDexAmountIn</c>/<c>encodeDexQuote</c>
/// in its <c>client.ts</c>) uses exactly these two endpoints and nothing else — the actual DEX
/// routing/quoting logic isn't even in <c>boltz-backend</c>'s own TypeScript; that repo's
/// <c>QuoteRouter.ts</c> opens with "The API is implemented in the sidecar", i.e. a separate
/// internal service. So this class is a thin REST wrapper reusing <see cref="BoltzClient"/>'s
/// existing generic <c>GetFromJsonAsync</c>/<c>PostAsJsonAsync</c> helpers (the same ones
/// <c>EvmChainSwapProvider</c> already uses for <c>/v2/swap/chain</c> etc.) — no Uniswap/Camelot
/// SDK dependency, no on-chain Quoter calls, no path selection: Boltz already picks the best
/// quote across whatever DEXes it aggregates and hands back calldata ready for
/// <c>Router.executeAndLockERC20WithPermit2</c>/<c>claimERC20Execute</c>.
/// </remarks>
public class BoltzDexQuoteProvider(
    BoltzClient boltzClient,
    RouterClient routerClient,
    string currency,
    decimal slippageTolerance = 0.01m) : IDexQuoteProvider
{
    /// <inheritdoc />
    public async Task<DexSwapQuote> GetSwapCallsAsync(
        string tokenIn, string tokenOut, BigInteger amountIn, CancellationToken ct = default)
    {
        var quotes = await boltzClient.GetFromJsonAsync<TokenQuoteResponse[]>(
            $"v2/quote/{currency}/in?tokenIn={tokenIn}&tokenOut={tokenOut}&amountIn={amountIn}", ct);

        if (quotes is not { Length: > 0 })
            throw new InvalidOperationException(
                $"Boltz returned no DEX quotes for {tokenIn} -> {tokenOut} (amountIn={amountIn}).");

        // /in is documented as sorted by highest output descending — first is best.
        var best = quotes[0];
        var amountOut = BigInteger.Parse(best.Quote);
        var amountOutMin = amountOut - (amountOut * (BigInteger)(slippageTolerance * 10_000m) / 10_000);

        var encoded = await boltzClient.PostAsJsonAsync<EncodeQuoteRequest, EncodeQuoteResponse>(
            $"v2/quote/{currency}/encode",
            new EncodeQuoteRequest
            {
                Recipient = routerClient.RouterAddress,
                AmountIn = amountIn.ToString(),
                AmountOutMin = amountOutMin.ToString(),
                Data = best.Data,
            },
            ct);

        var calls = encoded.Calls
            .Select(call => new Call
            {
                Target = call.To,
                Value = BigInteger.Parse(call.Value),
                CallData = call.Data.HexToByteArray(),
            })
            .ToList();

        return new DexSwapQuote(calls, amountOutMin);
    }
}
