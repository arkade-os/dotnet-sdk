using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Core.Wallet;

/// <summary>
/// An <see cref="IArkadeWalletSigner"/> composed of one or more <see cref="IDescriptorSigningSource"/>s.
/// For every signing call the composite resolves the first source whose
/// <see cref="IDescriptorSigningSource.CanProvideAsync"/> returns <c>true</c> and dispatches
/// the operation to it. Source order is significant — earlier sources take precedence — so
/// callers should register the cheapest / most-local source first and any fallbacks
/// (e.g. remote-signer transports) last.
/// </summary>
public class CompositeArkadeWalletSigner : IArkadeWalletSigner
{
    private readonly IReadOnlyList<IDescriptorSigningSource> _sources;

    public CompositeArkadeWalletSigner(IEnumerable<IDescriptorSigningSource> sources)
    {
        _sources = (sources ?? throw new ArgumentNullException(nameof(sources))).ToArray();
        if (_sources.Count == 0)
            throw new ArgumentException("At least one signing source is required.", nameof(sources));
    }

    public CompositeArkadeWalletSigner(params IDescriptorSigningSource[] sources)
        : this((IEnumerable<IDescriptorSigningSource>)sources)
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

    private async Task<IDescriptorSigningSource> ResolveAsync(OutputDescriptor descriptor, CancellationToken cancellationToken)
    {
        foreach (var source in _sources)
        {
            if (await source.CanProvideAsync(descriptor, cancellationToken).ConfigureAwait(false))
                return source;
        }

        throw new InvalidOperationException(
            $"No registered IDescriptorSigningSource claims descriptor '{descriptor}'. " +
            "Either the wallet is watch-only for this path, or a signing source is missing from the composition.");
    }
}
