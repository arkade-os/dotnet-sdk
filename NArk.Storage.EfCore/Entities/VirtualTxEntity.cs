using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NArk.Storage.EfCore.Entities;

public class VirtualTxEntity
{
    public string Txid { get; set; } = "";
    public string? Hex { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    public virtual ICollection<VtxoBranchEntity> Branches { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<VirtualTxEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.VirtualTxsTable, options.Schema);
        builder.HasKey(e => e.Txid);
        builder.Property(e => e.Hex).HasDefaultValue(null);
        builder.Property(e => e.ExpiresAt).HasDefaultValue(null);
    }
}
