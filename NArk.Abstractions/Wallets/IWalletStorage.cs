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

    /// <summary>
    /// Sparse-update one key in the wallet's <see cref="ArkWalletInfo.Metadata"/>
    /// JSON store. Pass <c>value: null</c> to remove the key. Concurrent calls
    /// targeting different keys are safe — the implementation must not clobber
    /// keys it didn't touch.
    /// </summary>
    /// <remarks>
    /// Designed for per-wallet bookkeeping the SDK accumulates over time (sync
    /// cursors, recovery state, etc.) without requiring a schema migration per
    /// concern. Throws if the wallet doesn't exist.
    /// </remarks>
    Task SetMetadataValue(string walletId, string key, string? value, CancellationToken ct = default);
}
