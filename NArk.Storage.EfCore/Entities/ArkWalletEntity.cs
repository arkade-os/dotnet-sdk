using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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

        builder.HasMany(w => w.Contracts)
            .WithOne(c => c.Wallet)
            .HasForeignKey(c => c.WalletId);

        builder.HasMany(w => w.Swaps)
            .WithOne(s => s.Wallet)
            .HasForeignKey(s => s.WalletId);
    }
}
