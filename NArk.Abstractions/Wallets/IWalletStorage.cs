namespace NArk.Abstractions.Wallets;

/// <summary>Persistence for wallet records.</summary>
public interface IWalletStorage
{
    /// <summary>Raised after a wallet is created or updated.</summary>
    event EventHandler<ArkWalletInfo>? WalletSaved;
    /// <summary>Raised after a wallet is deleted, carrying the wallet ID.</summary>
    event EventHandler<string>? WalletDeleted;

    /// <summary>Loads a wallet by ID or public-key fingerprint. Throws if not found.</summary>
    Task<ArkWalletInfo> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken ct = default);
    /// <summary>Returns all wallets in storage.</summary>
    Task<IReadOnlySet<ArkWalletInfo>> LoadAllWallets(CancellationToken ct = default);
    /// <summary>Creates or replaces a wallet record.</summary>
    Task SaveWallet(ArkWalletInfo wallet, CancellationToken ct = default);
    /// <summary>Updates the HD derivation high-water mark for the wallet.</summary>
    Task UpdateLastUsedIndex(string walletId, int lastUsedIndex, CancellationToken ct = default);

    /// <summary>Returns the wallet with the given ID, or null.</summary>
    Task<ArkWalletInfo?> GetWalletById(string walletId, CancellationToken ct = default);
    /// <summary>Returns wallets matching the given IDs; silently omits IDs not found.</summary>
    Task<IReadOnlyList<ArkWalletInfo>> GetWalletsByIds(IEnumerable<string> walletIds, CancellationToken ct = default);
    /// <summary>Inserts or optionally updates a wallet. Returns true when a new record was created.</summary>
    Task<bool> UpsertWallet(ArkWalletInfo wallet, bool updateIfExists = true, CancellationToken ct = default);
    /// <summary>Deletes the wallet. Returns true if it existed.</summary>
    Task<bool> DeleteWallet(string walletId, CancellationToken ct = default);
    /// <summary>Updates the wallet's sweep destination address. Pass null to clear it.</summary>
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
