namespace NArk.Core.Transport;

/// <summary>Describes why the Arkade server-info cache was invalidated or refreshed.</summary>
public enum ServerInfoChangedReason
{
    /// <summary>The cache was cleared by an explicit call to <see cref="IServerInfoCacheInvalidation.InvalidateServerInfoCache"/>.</summary>
    ManualInvalidation,
    /// <summary>The Arkade server rejected a request because the cached digest no longer matched its current configuration.</summary>
    DigestMismatch,
    /// <summary>The cache TTL expired and a fresh fetch observed a changed digest, indicating the server configuration was updated.</summary>
    TtlExpiry,
}

/// <summary>
/// Provides context for a <see cref="IServerInfoCacheInvalidation.ServerInfoChanged"/> event.
/// </summary>
public sealed class ServerInfoChangedEventArgs : EventArgs
{
    /// <summary>The reason this event was raised.</summary>
    public ServerInfoChangedReason Reason { get; init; } = ServerInfoChangedReason.ManualInvalidation;
    /// <summary>The digest that was cached before the change, or <c>null</c> if unavailable.</summary>
    public string? PreviousDigest { get; init; }
    /// <summary>The digest of the newly fetched server info, or <c>null</c> if unavailable.</summary>
    public string? NewDigest { get; init; }
}

/// <summary>
/// Capability exposed by <see cref="CachingClientTransport"/> so in-process consumers
/// (e.g. the plugin's contract-reconciliation service) can react to a server-info change.
/// Same shape as IVtxoStorage.VtxosChanged.
/// </summary>
public interface IServerInfoCacheInvalidation
{
    event EventHandler<ServerInfoChangedEventArgs>? ServerInfoChanged;
    // Optional arg keeps every existing parameterless caller (incl. #131's digest path) compiling.
    void InvalidateServerInfoCache(ServerInfoChangedEventArgs? args = null);
}
