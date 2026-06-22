using NArk.Abstractions.Wallets;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Core.Extensions;

internal static class WalletProviderExtensions
{
    /// <summary>
    /// Returns the signer for <paramref name="walletId"/>, or throws <see cref="InvalidOperationException"/>
    /// if the wallet has no signer (watch-only or remote signer unavailable).
    /// </summary>
    /// <param name="message">Optional error message override; defaults to a standard "no signer" message.</param>
    internal static async Task<IArkadeWalletSigner> GetSignerOrThrowAsync(
        this IWalletProvider walletProvider,
        string walletId,
        CancellationToken cancellationToken = default,
        string? message = null)
    {
        return await walletProvider.GetSignerAsync(walletId, cancellationToken)
            ?? throw new InvalidOperationException(message ?? $"Wallet '{walletId}' has no signer.");
    }

    /// <summary>
    /// Returns the signer and its compressed public key for the given descriptor in one call.
    /// Throws <see cref="InvalidOperationException"/> if the wallet has no signer.
    /// </summary>
    internal static async Task<(IArkadeWalletSigner Signer, ECPubKey PubKey)> GetSignerAndPubKeyAsync(
        this IWalletProvider walletProvider,
        string walletId,
        OutputDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var signer = await walletProvider.GetSignerOrThrowAsync(walletId, cancellationToken);
        var pubKey = await signer.GetPubKey(descriptor, cancellationToken);
        return (signer, pubKey);
    }
}
