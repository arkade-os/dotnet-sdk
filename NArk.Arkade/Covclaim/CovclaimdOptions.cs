namespace NArk.Arkade.Covclaim;

/// <summary>
/// Configuration for the <c>covclaimd</c> claim-daemon client.
/// </summary>
public sealed class CovclaimdOptions
{
    /// <summary>Named <see cref="System.Net.Http.HttpClient"/> used by <see cref="CovclaimdClient"/>.</summary>
    public const string HttpClientName = "covclaimd";

    /// <summary>
    /// Base address of the daemon's REST gateway, e.g.
    /// <c>http://localhost:7271</c>. Required.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// How long the daemon keeps a reveal registration before dropping it.
    /// Defaults to 15 minutes, matching covclaimd's built-in TTL.
    /// </summary>
    /// <remarks>
    /// The registry is in-memory, so a daemon restart also drops registrations
    /// early. Callers that need a swap covered for longer than one TTL must
    /// re-register; this value is exposed so a re-registration loop can be paced
    /// off it rather than hard-coding the same constant twice.
    /// </remarks>
    public TimeSpan RegistrationTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Per-request timeout. Defaults to 10 seconds.
    /// </summary>
    /// <remarks>
    /// Deliberately far below <see cref="System.Net.Http.HttpClient"/>'s 100 second
    /// default. Registering a claim happens inline while a swap is being created, so an
    /// unreachable daemon would otherwise stall swap initiation for a minute and a half
    /// — and the caller treats registration failures as non-fatal precisely because the
    /// swap should not depend on the daemon being up.
    /// </remarks>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether the daemon's keys may be cached for the lifetime of the client.
    /// They are static per daemon instance, so this is on by default; turn it off
    /// if a deployment rotates them without restarting consumers.
    /// </summary>
    public bool CacheKeys { get; set; } = true;
}
