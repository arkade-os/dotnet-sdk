using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NArk.Abstractions.Payments;

namespace NArk.Storage.EfCore.Entities;

public class ArkPaymentEntity
{
    public string PaymentId { get; set; } = "";
    public string WalletId { get; set; } = "";
    public string Recipient { get; set; } = "";
    public long Amount { get; set; }
    public ArkPaymentMethod Method { get; set; }
    public ArkPaymentStatus Status { get; set; }
    public string? FailReason { get; set; }

    /// <summary>
    /// Intent transaction ID — proof for ArkSend payments.
    /// </summary>
    public string? IntentTxId { get; set; }

    /// <summary>
    /// Swap ID — proof for SubmarineSwap and ChainSwap payments.
    /// </summary>
    public string? SwapId { get; set; }

    /// <summary>
    /// On-chain transaction ID — proof for CollaborativeExit payments.
    /// </summary>
    public string? OnchainTxId { get; set; }

    /// <summary>
    /// JSON array of assets transferred with this payment.
    /// </summary>
    [Column("Assets", TypeName = "jsonb")]
    public string? AssetsJson { get; set; }

    [NotMapped]
    public IReadOnlyList<NArk.Abstractions.VTXOs.VtxoAsset>? Assets
    {
        get => AssetsJson is null ? null : JsonSerializer.Deserialize<List<NArk.Abstractions.VTXOs.VtxoAsset>>(AssetsJson);
        set => AssetsJson = value is null ? null : JsonSerializer.Serialize(value);
    }

    [Column("Metadata", TypeName = "jsonb")]
    public string? MetadataJson { get; set; }

    [NotMapped]
    public Dictionary<string, string>? Metadata
    {
        get => MetadataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson);
        set => MetadataJson = value is null ? null : JsonSerializer.Serialize(value);
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    // Navigation
    public ArkWalletEntity Wallet { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<ArkPaymentEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.PaymentsTable, options.Schema);
        builder.HasKey(e => e.PaymentId);
        builder.Property(e => e.WalletId).IsRequired();
        builder.Property(e => e.Recipient).IsRequired();
        builder.Property(e => e.Method).IsRequired();
        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.FailReason).HasDefaultValue(null);
        builder.Property(e => e.IntentTxId).HasDefaultValue(null);
        builder.Property(e => e.SwapId).HasDefaultValue(null);
        builder.Property(e => e.OnchainTxId).HasDefaultValue(null);
        builder.Property(e => e.AssetsJson).HasDefaultValue(null);
        builder.Property(e => e.MetadataJson).HasDefaultValue(null);

        builder.HasOne(e => e.Wallet)
            .WithMany()
            .HasForeignKey(e => e.WalletId);

        builder.HasIndex(e => e.WalletId);
    }
}
