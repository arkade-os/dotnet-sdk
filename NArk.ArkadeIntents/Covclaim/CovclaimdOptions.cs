namespace NArk.ArkadeIntents.Covclaim;

/// <summary>How to reach the covclaimd instance this wallet reveals its claims to.</summary>
/// <remarks>
/// Optional on every corridor: without it a receive still works, because the wallet claims its own
/// lockup once the monitor sees it funded. This configures a second claimant, not a dependency.
/// </remarks>
public sealed class CovclaimdOptions
{
    /// <summary>The named <see cref="HttpClient"/> the client resolves.</summary>
    public const string HttpClientName = "covclaimd";

    /// <summary>Base address of the daemon's REST gateway.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// How long a reveal stays registered before the daemon drops it. An upper bound only — the
    /// daemon holds reveals in memory, so a restart loses them. Renewal paces itself off this value.
    /// </summary>
    public TimeSpan RegistrationTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long any one request may take. Short on purpose: a second claimant must never be the
    /// reason a receive about to hand out an invoice stalls.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether to remember the daemon's keys for the process's lifetime.</summary>
    /// <remarks>
    /// Off by default: covclaimd generates its encryption key at startup, and sealing to a stale one
    /// fails silently — only the daemon can tell the AEAD tag does not check out, so the renewal loop
    /// keeps reporting success with the second claimant gone. Turn it on only where the daemon's
    /// lifetime is known to outlive this process's.
    /// </remarks>
    public bool CacheKeys { get; set; }

    /// <summary>
    /// Allow plain HTTP to a non-loopback daemon. Off by default, and rarely the right answer.
    /// </summary>
    /// <remarks>
    /// Whoever can answer for this endpoint substitutes their own key and reads every secret revealed
    /// to it — and a preimage in someone else's hands settles the payer's invoice while the lockup is
    /// reclaimed at <c>refund_locktime</c>. Loopback is exempt: nothing crosses a wire.
    /// </remarks>
    public bool AllowInsecureHttp { get; set; }
}
