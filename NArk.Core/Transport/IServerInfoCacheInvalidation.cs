namespace NArk.Core.Transport;

public enum ServerInfoChangedReason { ManualInvalidation, DigestMismatch, TtlExpiry }

public sealed class ServerInfoChangedEventArgs : EventArgs
{
    public ServerInfoChangedReason Reason { get; init; } = ServerInfoChangedReason.ManualInvalidation;
    public string? PreviousDigest { get; init; }
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
