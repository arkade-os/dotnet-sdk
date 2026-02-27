namespace NArk.Swaps.Abstractions;

public interface ISwapProvider : IAsyncDisposable
{
    string ProviderId { get; }
    string DisplayName { get; }

    bool SupportsRoute(SwapRoute route);
    Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct);

    Task StartAsync(string walletId, CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct);
    Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct);

    event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;
}
