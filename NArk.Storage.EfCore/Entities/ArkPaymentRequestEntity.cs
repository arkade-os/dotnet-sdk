using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NArk.Abstractions.Payments;

namespace NArk.Storage.EfCore.Entities;

public class ArkPaymentRequestEntity
{
    public string RequestId { get; set; } = "";
    public string WalletId { get; set; } = "";
    public long? Amount { get; set; }
    public string? Description { get; set; }
    public ArkPaymentRequestStatus Status { get; set; }
    public long ReceivedAmount { get; set; }
    public long Overpayment { get; set; }

    public string? ArkAddress { get; set; }
    public string? BoardingAddress { get; set; }
    public string? LightningInvoice { get; set; }

    /// <summary>
    /// JSON array of contract script hex strings being watched.
    /// </summary>
    [Column("ContractScripts", TypeName = "jsonb")]
    public string ContractScriptsJson { get; set; } = "[]";

    [NotMapped]
    public string[] ContractScripts
    {
        get => JsonSerializer.Deserialize<string[]>(ContractScriptsJson) ?? [];
        set => ContractScriptsJson = JsonSerializer.Serialize(value);
    }

    /// <summary>
    /// Swap ID for the reverse submarine swap (Lightning option).
    /// </summary>
    public string? SwapId { get; set; }

    /// <summary>
    /// Expected asset for this payment request (JSON).
    /// </summary>
    [Column("ExpectedAsset", TypeName = "jsonb")]
    public string? ExpectedAssetJson { get; set; }

    [NotMapped]
    public NArk.Abstractions.VTXOs.VtxoAsset? ExpectedAsset
    {
        get => ExpectedAssetJson is null ? null : JsonSerializer.Deserialize<NArk.Abstractions.VTXOs.VtxoAsset>(ExpectedAssetJson);
        set => ExpectedAssetJson = value is null ? null : JsonSerializer.Serialize(value);
    }

    /// <summary>
    /// Assets received so far (JSON array).
    /// </summary>
    [Column("ReceivedAssets", TypeName = "jsonb")]
    public string? ReceivedAssetsJson { get; set; }

    [NotMapped]
    public IReadOnlyList<NArk.Abstractions.VTXOs.VtxoAsset>? ReceivedAssets
    {
        get => ReceivedAssetsJson is null ? null : JsonSerializer.Deserialize<List<NArk.Abstractions.VTXOs.VtxoAsset>>(ReceivedAssetsJson);
        set => ReceivedAssetsJson = value is null ? null : JsonSerializer.Serialize(value);
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
    public DateTimeOffset? ExpiresAt { get; set; }

    // Navigation
    public ArkWalletEntity Wallet { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<ArkPaymentRequestEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.PaymentRequestsTable, options.Schema);
        builder.HasKey(e => e.RequestId);
        builder.Property(e => e.WalletId).IsRequired();
        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.ContractScriptsJson).IsRequired();
        builder.Property(e => e.ExpectedAssetJson).HasDefaultValue(null);
        builder.Property(e => e.ReceivedAssetsJson).HasDefaultValue(null);
        builder.Property(e => e.MetadataJson).HasDefaultValue(null);

        builder.HasOne(e => e.Wallet)
            .WithMany()
            .HasForeignKey(e => e.WalletId);

        builder.HasIndex(e => e.WalletId);
    }
}
