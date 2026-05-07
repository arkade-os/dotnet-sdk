using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NArk.Abstractions.VirtualTxs;

namespace NArk.Storage.EfCore.Entities;

public class VirtualTxEntity
{
    public string Txid { get; set; } = "";
    public string? Hex { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Tx type as reported by arkd's chain indexer.</summary>
    public ChainedTxType Type { get; set; } = ChainedTxType.Unspecified;

    public virtual ICollection<VtxoBranchEntity> Branches { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<VirtualTxEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.VirtualTxsTable, options.Schema);
        builder.HasKey(e => e.Txid);
        builder.Property(e => e.Hex).HasDefaultValue(null);
        builder.Property(e => e.ExpiresAt).HasDefaultValue(null);
        builder.Property(e => e.Type)
            .HasDefaultValue(ChainedTxType.Unspecified)
            .HasConversion<int>();
    }
}
