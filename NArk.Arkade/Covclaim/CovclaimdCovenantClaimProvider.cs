using NArk.Arkade.Crypto;
using NArk.Core.Contracts;
using NBitcoin;

namespace NArk.Arkade.Covclaim;

/// <summary>
/// Implements <see cref="ICovenantClaimProvider"/> against a <c>covclaimd</c> claim
/// daemon: derives the destination-bound co-signer key, and registers claim
/// authorisations over the daemon's reveal endpoint.
/// </summary>
/// <remarks>
/// This is the only place the two halves meet — the ArkadeScript that pins the claim
/// destination, and the daemon that will act on it. Everything upstream sees a plain
/// <see cref="TaprootPubKey"/> and never handles the untweaked signer key, so the
/// classic failure of committing to a key nobody can sign for cannot be expressed.
/// </remarks>
public sealed class CovclaimdCovenantClaimProvider : ICovenantClaimProvider
{
    private readonly ICovclaimdClient _client;

    /// <param name="client">Client for the claim daemon that will perform the claims.</param>
    public CovclaimdCovenantClaimProvider(ICovclaimdClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public async Task<TaprootPubKey> GetCovenantClaimKeyAsync(
        Script claimDestination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claimDestination);

        var keys = await _client.GetKeysAsync(cancellationToken);
        var arkadeScript = CovenantClaimScript.EnforcePayTo(claimDestination);

        // Tweaking by the script's tagged hash is what binds the key to this exact
        // destination: the daemon's signer will only produce a signature for an input
        // whose attached script hashes to the same tweak.
        return ArkadeTweak.Tweak(keys.EmulatorTaprootPubKey, arkadeScript);
    }

    /// <inheritdoc />
    public Task RegisterAsync(
        string swapAddress,
        byte[] preimage,
        Script claimDestination,
        TapScript[] taptree,
        CancellationToken cancellationToken = default)
        => _client.RevealAsync(swapAddress, preimage, claimDestination, taptree, cancellationToken);
}
