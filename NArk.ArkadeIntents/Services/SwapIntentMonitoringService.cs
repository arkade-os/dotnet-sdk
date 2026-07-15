using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;

namespace NArk.Arkade.NonInteractiveSwaps;

/// <summary>
/// Tracks non-interactive swap intents and derives each one's terminal status from its covenant
/// VTXO's on-chain lifecycle. It does not open its own arkd subscription: it plugs into the shared
/// <c>VtxoSynchronizationService</c> as an <see cref="IActiveScriptsProvider"/> (so the covenant
/// scripts join the single watched set — one subscription, 5s safety-net poll, catch-up, reconnect)
/// and reacts to <see cref="IVtxoStorage.VtxosChanged"/>.
/// </summary>
/// <remarks>
/// A spent covenant VTXO means the solver fulfilled the swap; a swept one means it expired and the
/// deposit is recoverable. Only <see cref="SwapIntentStatus.Pending"/> intents transition — before
/// spending a cancel path a caller must move the intent to <see cref="SwapIntentStatus.Cancelling"/>
/// so the cancel-spend is never read as a fill (the same race guard the arkade wallet uses).
/// </remarks>
public sealed class SwapIntentMonitoringService : IActiveScriptsProvider, IDisposable
{
    private readonly IVtxoStorage _vtxoStorage;
    private readonly ILogger<SwapIntentMonitoringService>? _logger;

    /// <summary>Pending intents keyed by their covenant pkScript (the VtxoSync watch key).</summary>
    private readonly ConcurrentDictionary<string, SwapIntent> _pending = new();

    public SwapIntentMonitoringService(IVtxoStorage vtxoStorage, ILogger<SwapIntentMonitoringService>? logger = null)
    {
        _vtxoStorage = vtxoStorage;
        _logger = logger;
        _vtxoStorage.VtxosChanged += OnVtxoChanged;
    }

    /// <summary>Raised when a tracked intent reaches a terminal status (<see cref="SwapIntentStatus.Fulfilled"/>/<see cref="SwapIntentStatus.Recoverable"/>).</summary>
    public event EventHandler<SwapIntent>? IntentUpdated;

    /// <summary><see cref="IActiveScriptsProvider"/>: raised when the tracked script set changes so the sync service re-derives it.</summary>
    public event EventHandler? ActiveScriptsChanged;

    /// <summary>The currently-tracked (pending) swap intents.</summary>
    public IReadOnlyCollection<SwapIntent> Pending => _pending.Values.ToArray();

    /// <summary>
    /// Start tracking a pending swap: its covenant pkScript joins the active-scripts set, so the
    /// shared sync service subscribes to it and VTXO changes drive the transition. No-op for a
    /// non-pending intent or one already tracked.
    /// </summary>
    public void Track(SwapIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Status != SwapIntentStatus.Pending) return;
        if (_pending.TryAdd(intent.SwapPkScript, intent))
        {
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stop tracking a swap by its covenant pkScript (e.g. after a cancel completes).</summary>
    public void Untrack(string swapPkScript)
    {
        if (_pending.TryRemove(swapPkScript, out _))
        {
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public Task<HashSet<string>> GetActiveScripts(CancellationToken cancellationToken = default)
        => Task.FromResult(_pending.Keys.ToHashSet());

    /// <summary>Map a covenant VTXO's lifecycle to a terminal swap status, or <c>null</c> while still open.</summary>
    public static SwapIntentStatus? ResolveTerminalStatus(ArkVtxo vtxo)
    {
        if (vtxo.IsSpent()) return SwapIntentStatus.Fulfilled;
        if (vtxo.Swept) return SwapIntentStatus.Recoverable;
        return null;
    }

    private void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        if (!_pending.TryGetValue(vtxo.Script, out var intent)) return;
        // Race guard: a caller may have moved the intent to Cancelling before spending the cancel.
        if (intent.Status != SwapIntentStatus.Pending) return;

        var status = ResolveTerminalStatus(vtxo);
        if (status is null) return;

        // Claim the transition — only the thread that removes it applies the change and notifies,
        // so concurrent pushes for the same script can't double-fire.
        if (!_pending.TryRemove(vtxo.Script, out intent)) return;

        intent!.Status = status.Value;
        if (status == SwapIntentStatus.Fulfilled)
        {
            intent.SpentTxid = vtxo.ArkTxid ?? vtxo.SpentByTransactionId;
        }

        _logger?.LogInformation("Swap covenant {Script} → {Status}", vtxo.Script, status.Value);
        ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        IntentUpdated?.Invoke(this, intent);
    }

    public void Dispose() => _vtxoStorage.VtxosChanged -= OnVtxoChanged;
}
