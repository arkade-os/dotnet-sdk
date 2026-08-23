using NArk.Swaps.Abstractions;

namespace NArk.Swaps.Boltz;

public static class BoltzRouteHelper
{
    public static readonly SwapRoute[] Routes =
    [
        new (SwapAsset.ArkBtc, SwapAsset.BtcLightning),   // Submarine: ARK → LN
        new (SwapAsset.BtcLightning, SwapAsset.ArkBtc),   // Reverse:   LN  → ARK
        new (SwapAsset.ArkBtc, SwapAsset.BtcOnchain),     // Chain:     ARK → BTC
        new (SwapAsset.BtcOnchain, SwapAsset.ArkBtc),     // Chain:     BTC → ARK
    ];

    public static Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken _) =>
        Task.FromResult<IReadOnlyCollection<SwapRoute>>(Routes);

    public static bool SupportsRoute(SwapRoute route) => route switch
    {
        { Source.Network: SwapNetwork.Ark, Destination.Network: SwapNetwork.Lightning } => true,
        { Source.Network: SwapNetwork.Lightning, Destination.Network: SwapNetwork.Ark } => true,
        { Source.Network: SwapNetwork.Ark, Destination.Network: SwapNetwork.BitcoinOnchain } => true,
        { Source.Network: SwapNetwork.BitcoinOnchain, Destination.Network: SwapNetwork.Ark } => true,
        _ => false
    };

    public static async Task<SwapLimits> GetLimitsAsync(SwapRoute route, BoltzLimitsValidator limitsValidator, CancellationToken ct)
    {
        var isChain = route.Source.Network == SwapNetwork.BitcoinOnchain ||
                      route.Destination.Network == SwapNetwork.BitcoinOnchain;

        BoltzLimits? limits;
        if (isChain)
        {
            var isBtcToArk = route.Source.Network == SwapNetwork.BitcoinOnchain;
            limits = await limitsValidator.GetChainLimitsAsync(isBtcToArk, ct);
        }
        else
        {
            var isReverse = route.Source.Network == SwapNetwork.Lightning;
            limits = await limitsValidator.GetLimitsAsync(isReverse, ct);
        }

        if (limits is null)
            throw new InvalidOperationException($"Unable to fetch Boltz limits for route {route}");

        return new SwapLimits
        {
            Route = route,
            MinAmount = limits.MinAmount,
            MaxAmount = limits.MaxAmount,
            FeePercentage = limits.FeePercentage,
            MinerFee = limits.MinerFee
        };
    }

    public static async Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, BoltzLimitsValidator limitsValidator, CancellationToken ct)
    {
        var limits = await GetLimitsAsync(route, limitsValidator, ct);
        var fee = (long)(amount * limits.FeePercentage) + limits.MinerFee;
        return new SwapQuote
        {
            Route = route,
            SourceAmount = amount,
            DestinationAmount = amount - fee,
            TotalFees = fee,
            ExchangeRate = 1m
        };
    }
}