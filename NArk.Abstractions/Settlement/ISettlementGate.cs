namespace NArk.Abstractions.Settlement;

/// <summary>
/// A veto on settling a given wallet right now. Registered gates are consulted before
/// any policy runs; if any gate reports blocked, the wallet is skipped until the next
/// activity or heartbeat tick.
/// <para>
/// This is how a subsystem the settlement engine knows nothing about protects funds it
/// has already committed — <c>NArk.Swaps</c> uses it to hold settlement off while a
/// freshly created swap is still being funded, so the same VTXOs are not spent twice.
/// </para>
/// </summary>
public interface ISettlementGate
{
    /// <summary>
    /// Returns <see langword="true"/> to block settlement for <paramref name="walletId"/>.
    /// Implementations should be cheap and must not throw for expected conditions.
    /// </summary>
    Task<bool> IsBlockedAsync(string walletId, CancellationToken cancellationToken = default);
}
