using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NArk.Storage.EfCore.Entities;

public class ArkIntentVtxoEntity
{
    public string IntentTxId { get; set; } = "";
    public ArkIntentEntity Intent { get; set; } = null!;

    public string VtxoTransactionId { get; set; } = "";
    public int VtxoTransactionOutputIndex { get; set; }
    public VtxoEntity Vtxo { get; set; } = null!;

    public DateTimeOffset LinkedAt { get; set; }

    internal static void Configure(EntityTypeBuilder<ArkIntentVtxoEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.IntentVtxosTable, options.Schema);
        builder.HasKey(e => new { e.IntentTxId, e.VtxoTransactionId, e.VtxoTransactionOutputIndex });

        builder.HasOne(e => e.Vtxo)
            .WithMany(v => v.IntentVtxos)
            .HasForeignKey(e => new { e.VtxoTransactionId, e.VtxoTransactionOutputIndex })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.VtxoTransactionId, e.VtxoTransactionOutputIndex });
    }
}
