using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NArk.ArkadeIntents.Models;

namespace NArk.Storage.EfCore.Entities;

/// <summary>Persisted non-interactive swap intent (the Arkade BTC↔asset covenant swap).</summary>
public class ArkadeSwapIntentEntity
{
    /// <summary>Funding txid — the swap's identity.</summary>
    public string Id { get; set; } = "";

    public string WalletId { get; set; } = "";

    public ArkadeSwapIntentType Type { get; set; }

    /// <summary>Amount the maker deposits, in atomic units (sats for BTC).</summary>
    public long OfferAmount { get; set; }

    /// <summary>Amount the maker wants, in atomic units.</summary>
    public long WantAmount { get; set; }

    public ArkadeSwapIntentStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Hex pkScript of the swap covenant — the indexer monitoring key.</summary>
    public string SwapPkScript { get; set; } = "";

    public string SwapAddress { get; set; } = "";

    /// <summary>Hex-encoded offer TLV — rebuilds the covenant for the cancel path.</summary>
    public string OfferHex { get; set; } = "";

    /// <summary>The maker's signing output descriptor — the wallet-spendable cancel <c>$user</c> key.</summary>
    public string? MakerDescriptor { get; set; }

    public string? FromAssetId { get; set; }
    public string? ToAssetId { get; set; }

    /// <summary>The ark tx that fulfilled the swap; set once fulfilled.</summary>
    public string? SpentTxid { get; set; }

    public static void Configure(EntityTypeBuilder<ArkadeSwapIntentEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable("ArkadeSwapIntents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SwapPkScript).IsRequired();
        builder.HasIndex(x => x.SwapPkScript);
        builder.HasIndex(x => new { x.WalletId, x.Status });
    }
}
