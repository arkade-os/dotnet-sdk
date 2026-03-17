using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NArk.Abstractions.Exit;

namespace NArk.Storage.EfCore.Entities;

public class ExitSessionEntity
{
    public string Id { get; set; } = "";
    public string VtxoTxid { get; set; } = "";
    public int VtxoVout { get; set; }
    public string WalletId { get; set; } = "";
    public string ClaimAddress { get; set; } = "";
    public ExitSessionState State { get; set; }
    public int NextTxIndex { get; set; }
    public string? ClaimTxid { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? FailReason { get; set; }
    public int RetryCount { get; set; }

    // Navigation
    public VtxoEntity Vtxo { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<ExitSessionEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.ExitSessionsTable, options.Schema);
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.VtxoTxid, e.VtxoVout }).IsUnique();
        builder.Property(e => e.State).IsRequired();
        builder.Property(e => e.ClaimTxid).HasDefaultValue(null);
        builder.Property(e => e.FailReason).HasDefaultValue(null);

        builder.HasOne(e => e.Vtxo)
            .WithMany()
            .HasForeignKey(e => new { e.VtxoTxid, e.VtxoVout })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
