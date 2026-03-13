using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NArk.Swaps.Models;

namespace NArk.Storage.EfCore.Entities;

public class ArkSwapEntity
{
    public string SwapId { get; set; } = "";
    public string WalletId { get; set; } = "";

    public ArkSwapType SwapType { get; set; }

    public string Invoice { get; set; } = "";
    public long ExpectedAmount { get; set; }
    public string ContractScript { get; set; } = "";

    /// <summary>
    /// The address for the swap (derived from ContractScript).
    /// </summary>
    public string? Address { get; set; }

    public ArkSwapStatus Status { get; set; }

    /// <summary>
    /// Reason for failure if Status is Failed.
    /// </summary>
    public string? FailReason { get; set; }

    [Column("Metadata", TypeName = "jsonb")]
    public string? MetadataJson { get; set; }

    [NotMapped]
    public Dictionary<string, string>? Metadata
    {
        get => MetadataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson);
        set => MetadataJson = value is null ? null : JsonSerializer.Serialize(value);
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string Hash { get; set; } = "";

    // Navigation properties
    public ArkWalletContractEntity Contract { get; set; } = null!;
    public ArkWalletEntity Wallet { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<ArkSwapEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.SwapsTable, options.Schema);
        builder.HasKey(e => new { e.SwapId, e.WalletId });
        builder.Property(e => e.WalletId).IsRequired();
        builder.Property(e => e.SwapType).IsRequired();
        builder.Property(e => e.Invoice).IsRequired();
        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.FailReason).HasDefaultValue(null);
        builder.Property(e => e.Address).HasDefaultValue(null);
        builder.Property(e => e.MetadataJson).HasDefaultValue(null);

        builder.HasOne(e => e.Contract)
            .WithMany(c => c.Swaps)
            .HasForeignKey(e => new { e.ContractScript, e.WalletId });

        builder.HasOne(e => e.Wallet)
            .WithMany(w => w.Swaps)
            .HasForeignKey(e => e.WalletId);
    }
}
