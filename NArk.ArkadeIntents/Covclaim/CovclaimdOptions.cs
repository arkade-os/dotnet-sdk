namespace NArk.ArkadeIntents.Covclaim;

/// <summary>How to reach the covclaimd instance this wallet reveals its claims to.</summary>
/// <remarks>
/// Reaching covclaimd is optional on every corridor. Without it a receive still works — the wallet
/// claims its own lockup the moment the monitor sees it funded — so everything here configures a
/// second claimant racing ours, not a dependency of the swap.
/// </remarks>
public sealed class CovclaimdOptions
{
    /// <summary>The named <see cref="HttpClient"/> the client resolves.</summary>
    public const string HttpClientName = "covclaimd";

    /// <summary>Base address of the daemon's REST gateway.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// How long a reveal stays registered before the daemon drops it.
    /// </summary>
    /// <remarks>
    /// The daemon holds reveals in memory, so this is an upper bound and not a promise: a restart
    /// loses every registration it was holding. Renewal paces itself off this value.
    /// </remarks>
    public TimeSpan RegistrationTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long any one request may take before it is abandoned.</summary>
    /// <remarks>
    /// Short on purpose. Every call here sits on a path that has something better to do — a receive
    /// that is about to hand out an invoice, or a renewal pass with other swaps behind it — and a
    /// daemon that is merely a second claimant must never be the reason one of those stalls.
    /// </remarks>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether to remember the daemon's keys for the process's lifetime.</summary>
    /// <remarks>
    /// covclaimd generates its encryption key at startup, so a cached copy goes stale when the
    /// daemon restarts — and sealing to a stale key fails silently, since only the daemon can tell
    /// that the AEAD tag does not check out. Caching is still the default because the alternative
    /// is a round trip per swap; turn it off where the daemon restarts often enough to matter.
    /// </remarks>
    public bool CacheKeys { get; set; } = true;
}
