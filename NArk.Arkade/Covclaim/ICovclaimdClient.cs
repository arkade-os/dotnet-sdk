using NBitcoin;

namespace NArk.Arkade.Covclaim;

/// <summary>
/// Client for a <c>covclaimd</c> claim daemon — the bot that sweeps
/// preimage-gated VTXOs on our behalf so a swap still completes while our wallet
/// is offline.
/// </summary>
/// <remarks>
/// This SDK implements the maker (client) side only. The daemon watches arkd's
/// transaction stream, matches funded outputs, and builds the claim itself; we
/// just tell it what it needs to know and let it work.
/// </remarks>
public interface ICovclaimdClient
{
    /// <summary>
    /// Fetches the daemon's ECIES and emulator public keys.
    /// </summary>
    /// <remarks>
    /// Call this <em>before</em> constructing the swap contract: the emulator key
    /// must be baked into the covenant-claim leaf, so it has to be known at
    /// address-derivation time, not at reveal time.
    /// </remarks>
    Task<CovclaimdKeys> GetKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a claim packet for <paramref name="swapAddress"/>, authorising the
    /// daemon to claim that output the moment it is funded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The daemon accepts the registration only if <paramref name="taptree"/>
    /// hashes to <paramref name="swapAddress"/> <em>and</em> contains a
    /// condition-multisig leaf for this preimage and Arkade script. Because the
    /// packet is bound to the address it names, a third party who learns a swap
    /// address cannot register on its behalf, and re-registering the same address
    /// is harmless.
    /// </para>
    /// <para>
    /// Registrations are held in memory with a short TTL and are lost if the
    /// daemon restarts, so callers should re-register periodically for as long as
    /// the swap is unfunded rather than treating this as fire-and-forget. See
    /// <see cref="CovclaimdOptions.RegistrationTtl"/>.
    /// </para>
    /// </remarks>
    /// <param name="swapAddress">bech32m Arkade address of the VHTLC funding output.</param>
    /// <param name="preimage">The 32-byte swap preimage, encrypted to the daemon before sending.</param>
    /// <param name="claimDestination">
    /// P2TR scriptPubKey the claim must pay to. Must be the same destination
    /// committed to by the covenant leaf in <paramref name="taptree"/>.
    /// </param>
    /// <param name="taptree">Leaves of the funding output's taproot tree.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <exception cref="CovclaimdException">The daemon rejected the registration.</exception>
    Task RevealAsync(
        string swapAddress,
        byte[] preimage,
        Script claimDestination,
        TapScript[] taptree,
        CancellationToken cancellationToken = default);
}

/// <summary>Raised when a claim daemon rejects a request or is unreachable.</summary>
public sealed class CovclaimdException : Exception
{
    /// <summary>HTTP status returned by the daemon, when the failure came from a response.</summary>
    public int? StatusCode { get; }

    /// <inheritdoc />
    public CovclaimdException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
