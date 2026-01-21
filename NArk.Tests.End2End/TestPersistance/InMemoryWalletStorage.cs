using System.Collections.Concurrent;
using NArk.Abstractions.Wallets;
using NArk.Tests.End2End.Wallets;
using NArk.Transport;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemoryWalletProvider(IClientTransport transport) : IWalletProvider
{
    private readonly ConcurrentDictionary<string, SimpleSeedWallet> _wallets = new();

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
        return _wallets.GetValueOrDefault(identifier);
    }
}