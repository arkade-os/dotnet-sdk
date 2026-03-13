using NArk.Abstractions.Blockchain;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// Fallback chain time provider when no explorer is configured.
/// Uses current time and height 0 (VTXOs won't show as expired).
/// </summary>
public class FallbackChainTimeProvider : IChainTimeProvider
{
    public Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
        => Task.FromResult(new TimeHeight(DateTimeOffset.UtcNow, 0));
}
