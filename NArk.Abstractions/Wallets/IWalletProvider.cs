namespace NArk.Abstractions.Wallets;

/// <summary>Resolves signers and address providers for wallets by ID.</summary>
public interface IWalletProvider
{
    /// <summary>Returns the signer for the wallet, or null if the wallet is watch-only.</summary>
    Task<IArkadeWalletSigner?> GetSignerAsync(string identifier, CancellationToken cancellationToken = default);
    /// <summary>Returns the address provider for the wallet, or null if none is registered.</summary>
    Task<IArkadeAddressProvider?> GetAddressProviderAsync(string identifier, CancellationToken cancellationToken = default);
}