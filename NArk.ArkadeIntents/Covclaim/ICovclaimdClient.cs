using NBitcoin;

namespace NArk.ArkadeIntents.Covclaim;

/// <summary>
/// A covclaimd instance this wallet reveals its claims to, so a funded lockup is collected even
/// while the wallet is not running.
/// </summary>
/// <remarks>
/// The daemon is a <em>second</em> claimant, never the only one. It spends the covenant's
/// non-interactive claim leaf, which pins the payout to the script this wallet already gave the
/// solver, so the worst a hostile or broken daemon can do is fail to claim.
/// </remarks>
public interface ICovclaimdClient
{
    /// <summary>Read the daemon's published keys.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The encryption key a preimage is sealed to, and the emulator it claims through.</returns>
    /// <exception cref="CovclaimdException">The daemon is unreachable or answered badly.</exception>
    Task<CovclaimdKeys> GetKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a claim for a lockup, so the daemon collects it once it is funded. Repeat calls for
    /// the same address are safe, and are how a registration outlives the daemon's own TTL.
    /// </summary>
    /// <param name="swapAddress">The lockup's bech32m Arkade address.</param>
    /// <param name="preimage">The 32-byte secret, sealed to the daemon before it leaves here.</param>
    /// <param name="arkadeScript">
    /// The covenant's own claim script, taken from the contract rather than rebuilt: the covenant
    /// commits to a hash of it, and a copy rebuilt here strands the claim whenever the two disagree.
    /// </param>
    /// <param name="taptree">The lockup's leaves, including the claim leaf.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="CovclaimdException">The daemon is unreachable, or refused the registration.</exception>
    Task RevealAsync(
        string swapAddress,
        byte[] preimage,
        byte[] arkadeScript,
        TapScript[] taptree,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown when covclaimd cannot be reached, or answers with something unusable.</summary>
/// <remarks>
/// Worth catching rather than letting escape: the daemon is a convenience, and a swap that refused
/// to proceed because a second claimant was down would be worse off than one with only its own.
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
