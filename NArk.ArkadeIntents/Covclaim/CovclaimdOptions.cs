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
    /// <para>
    /// <b>Off by default</b>, and the reason is worth the round trip. covclaimd generates its
    /// encryption key at startup, so a cached copy goes stale the moment the daemon restarts — and
    /// sealing to a stale key fails <em>silently</em>, because only the daemon can tell that the
    /// AEAD tag does not check out. What an operator then sees is a renewal loop reporting success
    /// while the second claimant is gone.
    /// </para>
    /// <para>
    /// The reference client does not cache at all. Turn this on only where the daemon's lifetime is
    /// known to outlive this process's, and read <see cref="RegistrationTtl"/> as the window in
    /// which a stale key stays undetected.
    /// </para>
    /// </remarks>
    public bool CacheKeys { get; set; }

    /// <summary>
    /// Allow plain HTTP to a non-loopback daemon. Off by default, and rarely the right answer.
    /// </summary>
    /// <remarks>
    /// The key this daemon serves is the key a preimage gets sealed to, so whoever can answer for it
    /// can substitute their own and read every secret this wallet reveals. On a receive leg that is
    /// not a privacy loss but a funds one: a preimage in someone else's hands settles the payer's
    /// invoice without this wallet ever claiming, and the lockup is then reclaimed at
    /// <c>refund_locktime</c>. Loopback is exempt because that is where covclaimd runs by default
    /// and nothing crosses a wire.
    /// </remarks>
    public bool AllowInsecureHttp { get; set; }
}
