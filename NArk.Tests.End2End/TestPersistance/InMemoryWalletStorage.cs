using System.Collections.Concurrent;
using NArk.Abstractions.Wallets;
using NArk.Tests.End2End.Wallets;
using NArk.Core.Transport;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemoryWalletProvider(IClientTransport transport) : IWalletProvider
{
    private readonly ConcurrentDictionary<string, SimpleSeedWallet> _wallets = new();
    private readonly ConcurrentDictionary<string, IArkadeAddressProvider> _addressProviderOverrides = new();

    public async Task<string> CreateTestWallet()
    {
        var wallet = await SimpleSeedWallet.CreateNewWallet(transport, CancellationToken.None);
        _wallets.TryAdd(await wallet.GetWalletFingerprint(), wallet);
        return await wallet.GetWalletFingerprint();
    }

    public async Task<IArkadeWalletSigner?> GetSignerAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return _wallets.GetValueOrDefault(identifier);
    }

    public async Task<IArkadeAddressProvider?> GetAddressProviderAsync(string identifier, CancellationToken cancellationToken = default)
    {
        if (_addressProviderOverrides.TryGetValue(identifier, out var overrideProvider))
            return overrideProvider;
        return _wallets.GetValueOrDefault(identifier);
    }

    /// <summary>
    /// Override the address provider for a wallet (e.g. to wrap with <see cref="NArk.Core.Wallet.DelegatingAddressProvider"/>).
    /// </summary>
    public void SetAddressProvider(string identifier, IArkadeAddressProvider provider)
    {
        _addressProviderOverrides[identifier] = provider;
    }

    /// <summary>
    /// Gets the underlying SimpleSeedWallet for testing purposes.
    /// </summary>
    public SimpleSeedWallet? GetTestWallet(string identifier)
    {
        return _wallets.GetValueOrDefault(identifier);
    }
}