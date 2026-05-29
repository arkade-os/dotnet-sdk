using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Core.Wallet;

/// <summary>
/// Pure proxy implementation of <see cref="IArkadeWalletSigner"/> that
/// forwards every call to an <see cref="IRemoteSignerTransport"/>.
/// <see cref="DefaultWalletProvider"/> instantiates this when the wallet
/// has no local signing material (<see cref="ArkWalletInfo.Secret"/> is
/// null/empty) <b>and</b> a registered transport's
/// <see cref="IRemoteSignerTransport.KnowsWalletAsync"/> claims the wallet;
/// when no transport claims it, the wallet is treated as watch-only and
/// <see cref="IWalletProvider.GetSignerAsync"/> returns <c>null</c>.
/// </summary>
/// <remarks>
/// Holds no signing material — the <c>walletId</c> is captured at construction
/// time and tagged onto every transport call so a single transport instance
/// can serve multiple wallets.
/// </remarks>
public class RemoteArkadeWalletSigner(string walletId, IRemoteSignerTransport transport) : IArkadeWalletSigner
{
    private readonly string _walletId = walletId ?? throw new ArgumentNullException(nameof(walletId));
    private readonly IRemoteSignerTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public Task<ECPubKey> GetPubKey(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
        => _transport.GetPubKeyAsync(_walletId, descriptor, cancellationToken);

    public Task<MusigPartialSignature> SignMusig(
        OutputDescriptor descriptor,
        MusigContext context,
        CancellationToken cancellationToken = default)
        => _transport.SignMusigAsync(_walletId, descriptor, context, cancellationToken);

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(
        OutputDescriptor descriptor,
        uint256 hash,
        CancellationToken cancellationToken = default)
        => _transport.SignAsync(_walletId, descriptor, hash, cancellationToken);

    public Task<MusigPubNonce> GenerateNonces(
        OutputDescriptor descriptor,
        MusigContext context,
        CancellationToken cancellationToken = default)
        => _transport.GenerateNoncesAsync(_walletId, descriptor, context, cancellationToken);
}
