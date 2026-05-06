using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NArk.Abstractions.Wallets;

namespace NArk.Storage.EfCore.Entities;

public class ArkWalletEntity
{
    public string Id { get; set; } = "";

    /// <summary>
    /// For legacy wallets: the nsec private key.
    /// For HD wallets: the BIP-39 mnemonic.
    /// </summary>
    public string Wallet { get; set; } = "";

    /// <summary>
    /// Destination address for swept funds.
    /// </summary>
    public string? WalletDestination { get; set; }

    /// <summary>
    /// The type of wallet (Legacy nsec or HD mnemonic).
    /// Defaults to SingleKey for backwards compatibility.
    /// </summary>
    public WalletType WalletType { get; set; } = WalletType.SingleKey;

    /// <summary>
    /// For HD wallets: the account descriptor (e.g., tr([fingerprint/86'/0'/0']xpub...)).
    /// For legacy wallets: the simple tr(pubkey) descriptor.
    /// </summary>
    public string? AccountDescriptor { get; set; }

    /// <summary>
    /// For HD wallets: the last used derivation index.
    /// Incremented each time a new signing entity is created.
    /// </summary>
    public int LastUsedIndex { get; set; }

    /// <summary>
    /// Generic key-value store persisted as a JSON column. Used for per-wallet
    /// bookkeeping the SDK accumulates over time (sync cursors, recovery state,
    /// etc.) without requiring a column-add migration per concern.
    /// Updated via <see cref="NArk.Abstractions.Wallets.IWalletStorage.SetMetadataValue"/>.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; set; }

    public List<ArkWalletContractEntity> Contracts { get; set; } = [];
    public List<ArkSwapEntity> Swaps { get; set; } = [];

    internal static void Configure(EntityTypeBuilder<ArkWalletEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.WalletsTable, options.Schema);
        builder.HasKey(w => w.Id);
        builder.HasIndex(w => w.Wallet).IsUnique();
        builder.Property(w => w.WalletType).HasDefaultValue(WalletType.SingleKey);
        builder.Property(w => w.AccountDescriptor).HasDefaultValue("TODO_MIGRATION");
        builder.Property(w => w.LastUsedIndex).HasDefaultValue(0);

        // Stash Metadata as a JSON-serialized string column. Provider-agnostic
        // (Postgres jsonb / SQLite TEXT / SQL Server nvarchar(max)) — the
        // value-converter handles round-trip without binding to a specific
        // database's JSON support.
        builder.Property(w => w.Metadata)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null))
            .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, string>?>(
                (a, b) => ReferenceEquals(a, b) ||
                          (a != null && b != null && a.Count == b.Count && !a.Except(b).Any()),
                d => d == null ? 0 : d.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                d => d == null ? null : new Dictionary<string, string>(d)));

        builder.HasMany(w => w.Contracts)
            .WithOne(c => c.Wallet)
            .HasForeignKey(c => c.WalletId);

        builder.HasMany(w => w.Swaps)
            .WithOne(s => s.Wallet)
            .HasForeignKey(s => s.WalletId);
    }
}
