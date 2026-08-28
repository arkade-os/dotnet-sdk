using Microsoft.Extensions.Logging;
using NArk.Abstractions.Settlement;

namespace NArk.Core.Settlement;

/// <summary>
/// Routes a settlement to the first registered <see cref="ISettlementService"/> that is
/// available and accepts the destination.
/// <para>
/// Rails are tried in registration order, so a more specific rail must be registered
/// before a broader one. The built-in destination sweep claims only Arkade destinations
/// by default — on-chain Bitcoin needs
/// <see cref="SettlementOptions.EnableCollaborativeExit"/> — so an application's own
/// rails never have to out-race it.
/// </para>
/// </summary>
public class CompositeSettlementService(
    IEnumerable<ISettlementService> services,
    ILogger<CompositeSettlementService>? logger = null) : ISettlementService
{
    // Guards against a registration that also exposes this composite as an
    // ISettlementService, which would otherwise recurse forever.
    private IEnumerable<ISettlementService> Rails => services.Where(service => !ReferenceEquals(service, this));

    /// <summary>Registered rails, in the order they are tried.</summary>
    public IReadOnlyCollection<ISettlementService> RegisteredRails => [.. Rails];

    /// <inheritdoc />
    public bool Available => Rails.Any(rail => rail.Available);

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            if (Available)
                return null;

            var reasons = Rails
                .Select(rail => rail.UnavailableReason)
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .ToArray();

            return reasons.Length > 0
                ? string.Join("; ", reasons)
                : "No settlement rail is registered.";
        }
    }

    /// <inheritdoc />
    public bool CanSettle(SettlementDestination destination) =>
        Resolve(destination) is not null;

    /// <summary>
    /// Returns the rail that would handle <paramref name="destination"/>, or
    /// <see langword="null"/> when none is available for it.
    /// </summary>
    public ISettlementService? Resolve(SettlementDestination destination) =>
        Rails.FirstOrDefault(rail => rail.Available && rail.CanSettle(destination));

    /// <inheritdoc />
    public Task<SettlementResult> SettleAsync(
        SettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        var rail = Resolve(request.Destination);
        if (rail is null)
            throw new SettlementNotSupportedException(request.Destination, DescribeMissingRail(request.Destination));

        logger?.LogDebug(
            "Routing settlement of {Amount} {SourceAsset} for wallet {WalletId} to {Rail}",
            request.Amount, request.SourceAsset, request.WalletId, rail.GetType().Name);

        return rail.SettleAsync(request, cancellationToken);
    }

    private string DescribeMissingRail(SettlementDestination destination)
    {
        var target = $"{destination.Network}/{destination.Asset}";

        // A rail that handles the destination but is offline is a far more actionable
        // message than "nothing handles this", so say which and why.
        var unavailable = Rails
            .Where(rail => !rail.Available && rail.CanSettle(destination))
            .Select(rail => $"{rail.GetType().Name} ({rail.UnavailableReason ?? "unavailable"})")
            .ToArray();

        return unavailable.Length > 0
            ? $"No settlement rail is available for {target}. Matching but unavailable: {string.Join(", ", unavailable)}."
            : $"No settlement rail handles {target}. Register an ISettlementService that accepts it.";
    }
}
