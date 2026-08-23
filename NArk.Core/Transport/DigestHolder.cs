namespace NArk.Core.Transport;

/// <summary>
/// Holds the current Arkade server-info digest shared between a transport's
/// <c>GetServerInfoAsync</c> (writer) and its gRPC/HTTP pipeline (reader).
/// Individual reads and writes of the reference are atomic and visible across threads via
/// <c>volatile</c> — no lock is needed because no compound read-modify-write operation is performed.
/// </summary>
internal sealed class DigestHolder
{
    private volatile string? _digest;

    public string? Digest
    {
        get => _digest;
        set => _digest = value;
    }

    /// <summary>Clears the stored digest so subsequent requests are sent without <c>X-Digest</c>.</summary>
    public void Clear() => _digest = null;
}
