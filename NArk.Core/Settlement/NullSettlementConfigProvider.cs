using NArk.Abstractions.Settlement;

namespace NArk.Core.Settlement;

/// <summary>
/// The default <see cref="ISettlementConfigProvider"/> registered by
/// <c>AddArkSettlement()</c>: it returns no rules, so the settlement engine starts and
/// stays inert until the application registers a provider of its own.
/// </summary>
public class NullSettlementConfigProvider : ISettlementConfigProvider
{
    /// <inheritdoc />
    public Task<IReadOnlyCollection<SettlementConfig>> GetConfigs(
        string? walletId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<SettlementConfig>>([]);
}
