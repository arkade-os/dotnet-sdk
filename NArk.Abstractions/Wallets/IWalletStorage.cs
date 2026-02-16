namespace NArk.Abstractions.Wallets;

public interface IWalletStorage
{
    event EventHandler<ArkWalletInfo>? WalletSaved;
    event EventHandler<string>? WalletDeleted;

    Task<ArkWalletInfo> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken ct = default);
    Task<IReadOnlySet<ArkWalletInfo>> LoadAllWallets(CancellationToken ct = default);
    Task SaveWallet(ArkWalletInfo wallet, CancellationToken ct = default);
    Task UpdateLastUsedIndex(string walletId, int lastUsedIndex, CancellationToken ct = default);

    Task<ArkWalletInfo?> GetWalletById(string walletId, CancellationToken ct = default);
    Task<IReadOnlyList<ArkWalletInfo>> GetWalletsByIds(IEnumerable<string> walletIds, CancellationToken ct = default);
    Task<bool> UpsertWallet(ArkWalletInfo wallet, bool updateIfExists = true, CancellationToken ct = default);
    Task<bool> DeleteWallet(string walletId, CancellationToken ct = default);
    Task UpdateDestination(string walletId, string? destination, CancellationToken ct = default);
}
