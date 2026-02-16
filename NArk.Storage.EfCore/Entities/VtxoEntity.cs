using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NArk.Storage.EfCore.Entities;

public class VtxoEntity
{
    public string Script { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public int TransactionOutputIndex { get; set; }
    public string? SpentByTransactionId { get; set; }
    public string? SettledByTransactionId { get; set; }
    public long Amount { get; set; }
    public DateTimeOffset SeenAt { get; set; }
    public bool Recoverable { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public uint? ExpiresAtHeight { get; set; }
    public bool Preconfirmed { get; set; }
    public bool Unrolled { get; set; }
    public string? CommitmentTxids { get; set; }
    public string? ArkTxid { get; set; }

    public virtual ICollection<ArkIntentVtxoEntity> IntentVtxos { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<VtxoEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.VtxosTable, options.Schema);
        builder.HasKey(e => new { e.TransactionId, e.TransactionOutputIndex });
        builder.Property(e => e.SpentByTransactionId).HasDefaultValue(null);
        builder.Property(e => e.SettledByTransactionId).HasDefaultValue(null);
    }
}
