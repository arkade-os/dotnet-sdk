using System.Text.RegularExpressions;

namespace NArk.Arkade.Emulator;

/// <summary>
/// The covenant co-signer key each Arkade network runs, pinned rather than asked for.
/// </summary>
/// <remarks>
/// <para>
/// The emulator key is a property of the NETWORK, not of whoever happens to answer when you ask.
/// Every participant in a covenant has to commit to the same one, so reading it from an endpoint
/// means the endpoint decides what your money is locked to. A misconfigured address, a rotated
/// deployment, or something standing in the way all produce a contract that is well formed, an
/// address that looks ordinary, and a lockup the rest of the network will not honour — with the
/// mismatch surfacing only when a counterparty fails to claim.
/// </para>
/// <para>
/// Keyed on the name the Arkade server advertises, not on the Bitcoin network: mutinynet resolves
/// to <c>TestNet</c> here, so the two are indistinguishable by that alone and pinning by it would
/// silently serve one network's key on another.
/// </para>
/// <para>
/// Networks without an emulator deployment are absent deliberately, and asking for one throws.
/// There is no shape-based fallback because a wrong key cannot be detected downstream.
/// </para>
/// </remarks>
public static class EmulatorPubKeys
{
    /// <summary>Covenant co-signer for the mainnet Arkade deployment, compressed hex.</summary>
    public const string Bitcoin =
        "0239c196415da47b26456a101daaa12ba9e445bfe153197f1e2b750bf40e52092e";

    /// <summary>Covenant co-signer for the hosted mutinynet deployment, compressed hex.</summary>
    public const string Mutinynet =
        "03f823b9b2febc81f4af967e77aed2f541cbd3397c6d8f5a72e32eb7b471af889a";

    /// <summary>Covenant co-signer shipped with the <c>arkade-regtest</c> stack, compressed hex.</summary>
    public const string Regtest =
        "02999413c46fa10ada5cbc4bcc79a1d09160c2ba3cfc812705d7a13e5e545fb2a9";

    private static readonly Dictionary<string, string> ByNetwork = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bitcoin"] = Bitcoin,
        ["mainnet"] = Bitcoin,
        ["mutinynet"] = Mutinynet,
        ["regtest"] = Regtest,
    };

    /// <summary>The pinned key for <paramref name="networkName"/>, as the Arkade server names it.</summary>
    /// <param name="networkName">The server's own <c>network</c> value, e.g. <c>mutinynet</c>.</param>
    /// <returns>The compressed 33-byte key, hex.</returns>
    /// <exception cref="InvalidOperationException">No key is pinned for that network.</exception>
    public static string DefaultFor(string? networkName) =>
        networkName is { Length: > 0 } name && ByNetwork.TryGetValue(name, out var key)
            ? key
            : throw new InvalidOperationException(
                $"No emulator co-signer key is pinned for network '{networkName ?? "<unnamed>"}'. "
                + $"Pinned networks: {string.Join(", ", ByNetwork.Keys)}. Supply one explicitly if "
                + "this deployment runs an emulator of its own.");

    /// <summary>
    /// The key to build covenants against: the network's pin, or <paramref name="pubkeyOverride"/>
    /// when the caller supplies one.
    /// </summary>
    /// <param name="networkName">The server's own <c>network</c> value.</param>
    /// <param name="pubkeyOverride">A 33-byte compressed key, hex, or <c>null</c> for the pin.</param>
    /// <returns>The compressed 33-byte key, hex.</returns>
    /// <exception cref="InvalidOperationException">No key is pinned and none was supplied.</exception>
    /// <exception cref="ArgumentException"><paramref name="pubkeyOverride"/> is not a compressed key.</exception>
    /// <remarks>
    /// <para>
    /// Supplying an override means co-signing with a different service: every covenant built from
    /// the returned key can be completed by whoever holds it and by nobody else. It is the escape
    /// hatch for three situations, and the first is the one that matters operationally.
    /// </para>
    /// <para>
    /// 1. A network rotated its key and this SDK has not shipped the new constant. Nothing asks the
    /// service which key it signs with, so a rotation is invisible here until the constant is
    /// updated — covenants keep building against the retired key and it surfaces only when a claim
    /// is refused. Passing the new key restores service without waiting for a release, which makes
    /// a rotation a config change rather than an outage.
    /// 2. A private or self-hosted emulator, including on a network with no pin at all.
    /// 3. Tests and local stacks.
    /// </para>
    /// <para>
    /// A malformed override throws rather than being passed through: a typo would otherwise surface
    /// as an unspendable contract long after the fact.
    /// </para>
    /// </remarks>
    public static string Resolve(string? networkName, string? pubkeyOverride)
    {
        if (pubkeyOverride is null) return DefaultFor(networkName);

        if (!CompressedPubKey.IsMatch(pubkeyOverride))
        {
            throw new ArgumentException(
                "An emulator co-signer override must be 33-byte compressed secp256k1 hex "
                + $"(66 characters, 02 or 03 prefix), got '{pubkeyOverride}'.", nameof(pubkeyOverride));
        }

        return pubkeyOverride;
    }

    private static readonly Regex CompressedPubKey =
        new("^0[23][0-9a-fA-F]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Whether the key an emulator reports for itself is the one this network is pinned to.
    /// </summary>
    /// <param name="networkName">The server's own <c>network</c> value.</param>
    /// <param name="reported">What the emulator's <c>/v1/info</c> answered.</param>
    /// <returns><c>true</c> when they agree, or when nothing is pinned to compare against.</returns>
    /// <remarks>
    /// A diagnostic, not a step in deriving anything: nothing in this SDK reads the reported key to
    /// decide a covenant's co-signer. Reach for it when a claim is refused and the question is
    /// whether this deployment has rotated away from <see cref="DefaultFor"/> — which is the moment
    /// <see cref="Resolve"/>'s override exists for.
    /// </remarks>
    public static bool AgreesWithPin(string? networkName, string reported) =>
        !ByNetwork.TryGetValue(networkName ?? "", out var pinned)
        || string.Equals(pinned, reported, StringComparison.OrdinalIgnoreCase);
}
