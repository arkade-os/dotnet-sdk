using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NArk.Storage.EfCore.Entities;

public class VtxoBranchEntity
{
    public string VtxoTxid { get; set; } = "";
    public int VtxoVout { get; set; }
    public string VirtualTxid { get; set; } = "";
    public int Position { get; set; }

    // Navigation properties
    public VtxoEntity Vtxo { get; set; } = null!;
    public VirtualTxEntity VirtualTx { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<VtxoBranchEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.VtxoBranchesTable, options.Schema);
        builder.HasKey(e => new { e.VtxoTxid, e.VtxoVout, e.VirtualTxid });

        builder.HasOne(e => e.Vtxo)
            .WithMany()
            .HasForeignKey(e => new { e.VtxoTxid, e.VtxoVout })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.VirtualTx)
            .WithMany(v => v.Branches)
            .HasForeignKey(e => e.VirtualTxid)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
