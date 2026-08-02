namespace NArk.Swaps.Services;

/// <summary>
/// Controls how often <see cref="CovenantClaimRenewalService"/> refreshes covenant
/// claim authorisations.
/// </summary>
/// <remarks>
/// The defaults derive the cadence from whatever lifetime the covenant claim backend
/// advertises, so a signer with a different TTL is followed automatically rather than
/// needing this retuned.
/// </remarks>
public sealed class CovenantClaimRenewalOptions
{
    private double _renewalFraction = 0.75;

    /// <summary>
    /// Fixed renewal interval. When null (the default) the interval is
    /// <see cref="RenewalFraction"/> of the backend's advertised registration lifetime.
    /// </summary>
    /// <remarks>
    /// Set this only to pin a cadence independent of the backend — for a signer whose
    /// advertised lifetime is not trustworthy, or to slow down a shared daemon serving
    /// many wallets. A value shorter than <see cref="MinimumInterval"/> is still floored.
    /// </remarks>
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// Fraction of the backend's registration lifetime at which to renew. Defaults to
    /// <c>0.75</c>. Must be greater than 0 and at most 1.
    /// </summary>
    /// <remarks>
    /// Lower values cost more requests but tolerate more consecutive failures before
    /// cover lapses: at <c>0.75</c> a single failed pass can leave a gap before the next
    /// one lands, whereas <c>0.5</c> fits two attempts inside one lifetime.
    /// </remarks>
    public double RenewalFraction
    {
        get => _renewalFraction;
        set
        {
            if (value is <= 0 or > 1 || double.IsNaN(value))
                throw new ArgumentOutOfRangeException(nameof(value),
                    "Renewal fraction must be greater than 0 and at most 1.");
            _renewalFraction = value;
        }
    }

    /// <summary>
    /// Never renew more often than this, whatever the other settings work out to.
    /// Defaults to one minute.
    /// </summary>
    /// <remarks>
    /// Guards against a backend advertising an implausibly short lifetime turning the
    /// renewal loop into a hot loop against storage and the network.
    /// </remarks>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromMinutes(1);
}
