using NArk.Abstractions.VTXOs;
using NArk.Swaps.Models;

namespace NArk.Swaps.Abstractions;

/// <summary>
/// A swap provider implementation (Boltz, LendaSwap, etc.) that the router can
/// dispatch routes to. Providers expose their supported routes, quote/limits
/// metadata, and a lifecycle hook for background work (websocket subscriptions,
/// caches, etc.). Notification methods are default no-ops so providers can opt
/// in to VTXO/swap-change signals without forcing the router to type-check.
/// </summary>
public interface ISwapProvider : IAsyncDisposable
{
    string ProviderId { get; }
    string DisplayName { get; }

    bool SupportsRoute(SwapRoute route);
    Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct);

    /// <summary>
    /// Initialises any background work the provider needs (websocket, polling,
    /// cache warmup). Not bound to a specific wallet — providers manage their
    /// own per-wallet state via the notification hooks below.
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Graceful shutdown — cancel background tasks and await them before
    /// returning. Distinct from <see cref="IAsyncDisposable.DisposeAsync"/>
    /// so the host can drain in-flight work before final cleanup.
    /// </summary>
    Task StopAsync(CancellationToken ct);

    Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct);
    Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct);

    event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;

    /// <summary>
    /// Router calls this when an Arkade VTXO changes on a script the provider
    /// may care about. Default: no-op. Override in providers that need to
    /// react to VTXO arrival (e.g. detect a swap lockup landing).
    /// </summary>
    void NotifyVtxoChanged(ArkVtxo vtxo) { }

    /// <summary>
    /// Router calls this when a swap row is inserted/updated in storage.
    /// Default: no-op. Override in providers that maintain script→swap
    /// caches or status subscriptions.
    /// </summary>
    void NotifySwapChanged(ArkSwap swap) { }
}
