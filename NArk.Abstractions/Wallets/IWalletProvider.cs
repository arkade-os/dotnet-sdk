namespace NArk.Abstractions.Wallets;

public interface IWalletProvider
{
    Task<IArkadeWalletSigner?> GetSignerAsync(string identifier, CancellationToken cancellationToken = default);
    Task<IArkadeAddressProvider?> GetAddressProviderAsync(string identifier, CancellationToken cancellationToken = default);
}