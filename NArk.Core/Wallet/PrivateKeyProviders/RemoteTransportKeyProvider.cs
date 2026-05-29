using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Core.Wallet.PrivateKeyProviders;

/// <summary>
/// Pure proxy provider over an <see cref="IRemoteSignerTransport"/>: holds the
/// <c>walletId</c> the transport is keyed by, forwards every operation, and holds no signing
/// state of its own (the transport retains MuSig2 secret nonces on its side).
/// </summary>
/// <remarks>
/// <see cref="CanProvideAsync"/> delegates to <see cref="IRemoteSignerTransport.KnowsWalletAsync"/>,
/// which answers at wallet granularity, not per descriptor. A transport that "knows" the wallet
/// claims every descriptor under it; this is the right granularity when the transport replaces
/// the local signer entirely. For hybrid setups (local for some descriptors, remote for others),
/// the transport interface would need a finer-grained probe — out of scope here.
/// </remarks>
public class RemoteTransportKeyProvider : IPrivateKeyProvider
{
    private readonly IRemoteSignerTransport _transport;
    private readonly string _walletId;

    public RemoteTransportKeyProvider(IRemoteSignerTransport transport, string walletId)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _walletId = walletId ?? throw new ArgumentNullException(nameof(walletId));
    }

    public Task<bool> CanProvideAsync(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
        => _transport.KnowsWalletAsync(_walletId, cancellationToken);

    public Task<ECPubKey> GetPubKeyAsync(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
        => _transport.GetPubKeyAsync(_walletId, descriptor, cancellationToken);

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> SignAsync(OutputDescriptor descriptor, uint256 hash, CancellationToken cancellationToken = default)
        => _transport.SignAsync(_walletId, descriptor, hash, cancellationToken);

    public Task<MusigPubNonce> GenerateNoncesAsync(OutputDescriptor descriptor, MusigContext context,
        string sessionId, CancellationToken cancellationToken = default)
        => _transport.GenerateNoncesAsync(_walletId, descriptor, context, sessionId, cancellationToken);

    public Task<MusigPartialSignature> SignMusigAsync(OutputDescriptor descriptor, MusigContext context,
        string sessionId, CancellationToken cancellationToken = default)
        => _transport.SignMusigAsync(_walletId, descriptor, context, sessionId, cancellationToken);
}
