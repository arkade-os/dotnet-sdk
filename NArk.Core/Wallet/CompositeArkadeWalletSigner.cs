using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Core.Wallet;

/// <summary>
/// An <see cref="IArkadeWalletSigner"/> composed of one or more <see cref="IPrivateKeyProvider"/>s.
/// For every signing call the composite resolves the first provider whose
/// <see cref="IPrivateKeyProvider.CanProvideAsync"/> returns <c>true</c> and dispatches the
/// operation to it. Provider order is significant — earlier providers take precedence — so
/// callers should register the cheapest / most-local provider first and any fallbacks
/// (e.g. remote-signer transports) last.
/// </summary>
public class CompositeArkadeWalletSigner : IArkadeWalletSigner
{
    private readonly IReadOnlyList<IPrivateKeyProvider> _providers;

    public CompositeArkadeWalletSigner(IEnumerable<IPrivateKeyProvider> providers)
    {
        _providers = (providers ?? throw new ArgumentNullException(nameof(providers))).ToArray();
        if (_providers.Count == 0)
            throw new ArgumentException("At least one provider is required.", nameof(providers));
    }

    public CompositeArkadeWalletSigner(params IPrivateKeyProvider[] providers)
        : this((IEnumerable<IPrivateKeyProvider>)providers)
    {
    }

    public async Task<ECPubKey> GetPubKey(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
        => await (await ResolveAsync(descriptor, cancellationToken)).GetPubKeyAsync(descriptor, cancellationToken);

    public async Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(OutputDescriptor descriptor, uint256 hash, CancellationToken cancellationToken = default)
        => await (await ResolveAsync(descriptor, cancellationToken)).SignAsync(descriptor, hash, cancellationToken);

    public async Task<MusigPubNonce> GenerateNonces(OutputDescriptor descriptor, MusigContext context, string sessionId, CancellationToken cancellationToken = default)
        => await (await ResolveAsync(descriptor, cancellationToken)).GenerateNoncesAsync(descriptor, context, sessionId, cancellationToken);

    public async Task<MusigPartialSignature> SignMusig(OutputDescriptor descriptor, MusigContext context, string sessionId, CancellationToken cancellationToken = default)
        => await (await ResolveAsync(descriptor, cancellationToken)).SignMusigAsync(descriptor, context, sessionId, cancellationToken);

    private async Task<IPrivateKeyProvider> ResolveAsync(OutputDescriptor descriptor, CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            if (await provider.CanProvideAsync(descriptor, cancellationToken).ConfigureAwait(false))
                return provider;
        }

        throw new InvalidOperationException(
            $"No registered IPrivateKeyProvider claims descriptor '{descriptor}'. " +
            "Either the wallet is watch-only for this path, or a provider is missing from the composition.");
    }
}
