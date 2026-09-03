namespace NArk.Abstractions.Settlement;

/// <summary>
/// Executes a settlement: moves value out of a wallet to a
/// <see cref="SettlementDestination"/>.
/// <para>
/// One implementation per rail. The SDK ships a destination sweep (Arkade address or
/// collaborative exit) in <c>NArk.Core</c> and a BTC chain swap in <c>NArk.Swaps</c>;
/// applications add their own — a stablecoin transfer, an exchange deposit — by
/// registering another implementation whose <see cref="CanSettle"/> accepts that
/// network and asset. <c>CompositeSettlementService</c> routes between them.
/// </para>
/// </summary>
public interface ISettlementService
{
    /// <summary>
    /// Whether this rail can be used right now. A rail that is configured but
    /// temporarily unusable (provider offline, credentials missing) reports
    /// <see langword="false"/> and is skipped by routing rather than failing a settlement.
    /// </summary>
    bool Available { get; }

    /// <summary>Human-readable reason why <see cref="Available"/> is <see langword="false"/>; <see langword="null"/> when available.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Whether this rail handles <paramref name="destination"/>. Must be a cheap,
    /// side-effect-free check on network and asset.
    /// </summary>
    bool CanSettle(SettlementDestination destination);

    /// <summary>
    /// Executes the settlement. Throws when the transfer could not be started;
    /// once it returns, funds are considered committed to the destination.
    /// <para>
    /// Read <see cref="SettlementRequest.SourceAsset"/> before spending: the amount is
    /// denominated in it, so a rail that only handles BTC must reject a request whose
    /// source is an Arkade-issued asset even when the destination looks familiar.
    /// </para>
    /// </summary>
    /// <exception cref="SettlementNotSupportedException">The destination is not handled by this rail.</exception>
    Task<SettlementResult> SettleAsync(SettlementRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when no registered <see cref="ISettlementService"/> handles a destination,
/// or when one is asked to settle a destination it does not support.
/// </summary>
public class SettlementNotSupportedException(SettlementDestination destination, string message)
    : NotSupportedException(message)
{
    /// <summary>The destination that could not be settled.</summary>
    public SettlementDestination Destination { get; } = destination;
}
