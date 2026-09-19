using NBitcoin;

namespace NArk.ArkadeIntents.Covclaim;

/// <summary>
/// A covclaimd instance this wallet reveals its claims to, so a funded lockup is collected even
/// while the wallet is not running.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>reveal</b> path: the preimage goes straight to one named daemon over its own
/// endpoint, which registers the claim and watches for the lockup to be funded. The other path
/// stamps the packet into the funding transaction and lets any covclaimd find it on arkd's stream;
/// the two are independent and a deployment may run both.
/// </para>
/// <para>
/// Either way the daemon is a <em>second</em> claimant, never the only one. It spends the covenant's
/// non-interactive claim leaf, which pins the payout to the script this wallet already gave the
/// solver, so the worst a hostile or broken daemon can do is fail to claim — the wallet's own
/// claimer is still racing it, and whoever lands first pays the same address.
/// </para>
/// </remarks>
public interface ICovclaimdClient
{
    /// <summary>Read the daemon's published keys.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The encryption key a preimage is sealed to, and the emulator it claims through.</returns>
    /// <exception cref="CovclaimdException">The daemon is unreachable or answered badly.</exception>
    Task<CovclaimdKeys> GetKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a claim for a lockup, so the daemon collects it once it is funded.
    /// </summary>
    /// <param name="swapAddress">The lockup's bech32m Arkade address.</param>
    /// <param name="preimage">The 32-byte secret, sealed to the daemon before it leaves here.</param>
    /// <param name="arkadeScript">
    /// The covenant's own claim script, taken from the contract rather than rebuilt.
    /// </param>
    /// <param name="taptree">The lockup's leaves, including the claim leaf.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="CovclaimdException">The daemon is unreachable, or refused the registration.</exception>
    /// <remarks>
    /// <para>
    /// <paramref name="arkadeScript"/> is a parameter rather than something derived here, and that
    /// is the whole reason this signature differs from the obvious one. The covenant commits to a
    /// hash of that script, so the party that built the contract holds the only copy guaranteed to
    /// match it; a copy rebuilt here from the same inputs would be one more place for the two to
    /// disagree, and a disagreement strands the claim instead of failing it.
    /// </para>
    /// <para>
    /// Repeat calls for the same address are safe and are how a registration outlives the daemon's
    /// own TTL — see <see cref="CovclaimdOptions.RegistrationTtl"/>.
    /// </para>
    /// </remarks>
    Task RevealAsync(
        string swapAddress,
        byte[] preimage,
        byte[] arkadeScript,
        TapScript[] taptree,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown when covclaimd cannot be reached, or answers with something unusable.</summary>
/// <remarks>
/// Worth catching rather than letting escape on most paths: this daemon is a convenience, and a
/// swap that refused to proceed because a second claimant was down would be strictly worse off than
/// one that proceeded with only its own claimer.
/// </remarks>
public sealed class CovclaimdException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="statusCode">The HTTP status, when the daemon answered at all.</param>
    /// <param name="innerException">The transport failure underneath, if any.</param>
    public CovclaimdException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException) => StatusCode = statusCode;

    /// <summary>The HTTP status the daemon answered with, or <c>null</c> if it never answered.</summary>
    public int? StatusCode { get; }
}
