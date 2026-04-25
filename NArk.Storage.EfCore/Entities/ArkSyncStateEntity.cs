using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NArk.Storage.EfCore.Entities;

/// <summary>
/// Single-row table holding the SDK's persistent sync state markers.
/// At present only the VTXO last-full-poll timestamp lives here; future
/// scoped state (boltz cursor, on-chain block height, etc.) can be added
/// as additional columns without a new table.
/// </summary>
public class ArkSyncStateEntity
{
    /// <summary>
    /// Constant primary key — there is only ever one row.
    /// </summary>
    public const string SingletonId = "vtxo";

    public string Id { get; set; } = SingletonId;

    /// <summary>
    /// Wall-clock moment of the last full-set VTXO poll across the entire
    /// active-script view. Null until the first successful poll.
    /// </summary>
    public DateTimeOffset? LastFullPollAt { get; set; }

    internal static void Configure(EntityTypeBuilder<ArkSyncStateEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.SyncStateTable, options.Schema);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(64);
    }
}
