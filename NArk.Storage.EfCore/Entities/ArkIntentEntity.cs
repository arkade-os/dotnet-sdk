using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NArk.Abstractions.Intents;

namespace NArk.Storage.EfCore.Entities;

public class ArkIntentEntity
{
    /// <summary>
    /// The unique transaction ID for this intent (primary key).
    /// </summary>
    public string IntentTxId { get; set; } = "";

    public string? IntentId { get; set; }
    public string WalletId { get; set; } = "";
    public ArkIntentState State { get; set; }

    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ArkIntentVtxoEntity> IntentVtxos { get; set; } = [];

    public string RegisterProof { get; set; } = "";
    public string RegisterProofMessage { get; set; } = "";
    public string DeleteProof { get; set; } = "";
    public string DeleteProofMessage { get; set; } = "";

    public string? BatchId { get; set; }
    public string? CommitmentTransactionId { get; set; }
    public string? CancellationReason { get; set; }

    public string[] PartialForfeits { get; set; } = [];

    /// <summary>
    /// The output descriptor of the signing entity used for this intent.
    /// Required for HD wallets to look up the correct key for signing.
    /// </summary>
    public string? SignerDescriptor { get; set; }

    internal static void Configure(EntityTypeBuilder<ArkIntentEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.IntentsTable, options.Schema);
        builder.HasKey(e => e.IntentTxId);
        builder.HasIndex(e => e.IntentId).IsUnique().HasFilter("\"IntentId\" IS NOT NULL");
        builder.Property(e => e.BatchId).HasDefaultValue(null);
        builder.Property(e => e.CommitmentTransactionId).HasDefaultValue(null);
        builder.Property(e => e.CancellationReason).HasDefaultValue(null);
        builder.Property(e => e.SignerDescriptor).HasDefaultValue(null);

        builder.HasMany(e => e.IntentVtxos)
            .WithOne(e => e.Intent)
            .HasForeignKey(e => e.IntentTxId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
