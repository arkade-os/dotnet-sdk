using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NArk.ArkadeIntents.Models;

namespace NArk.Storage.EfCore.Entities;

/// <summary>Persisted non-interactive swap intent (the Arkade BTC↔asset covenant swap).</summary>
public class ArkadeSwapIntentEntity
{
    /// <summary>Funding txid — the swap's identity.</summary>
    public string Id { get; set; } = "";

    public string WalletId { get; set; } = "";

    public ArkadeSwapIntentType Type { get; set; }

    /// <summary>Amount the maker deposits, in atomic units (sats for BTC).</summary>
    public long OfferAmount { get; set; }

    /// <summary>Amount the maker wants, in atomic units.</summary>
    public long WantAmount { get; set; }

    public ArkadeSwapIntentStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Hex pkScript of the swap covenant — the indexer monitoring key.</summary>
    public string SwapPkScript { get; set; } = "";

    public string SwapAddress { get; set; } = "";

    public string? FromAssetId { get; set; }
    public string? ToAssetId { get; set; }

    /// <summary>The invoice's payment hash (hex) — a solver's natural key for the negotiation.</summary>
    public string? PaymentHash { get; set; }

    /// <summary>Unix seconds at which a Lightning swap's covenant refund path opens.</summary>
    public long? RefundLocktime { get; set; }

    /// <summary>Corridor-specific state, stored as one JSON column.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>The ark tx that fulfilled the swap; set once fulfilled.</summary>
    public string? SpentTxid { get; set; }

    public static void Configure(EntityTypeBuilder<ArkadeSwapIntentEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable("ArkadeSwapIntents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SwapPkScript).IsRequired();
        builder.HasIndex(x => x.SwapPkScript);
        builder.HasIndex(x => new { x.WalletId, x.Status });
        // A solver dedupes a Lightning negotiation on the payment hash, so we look swaps up by it.
        builder.HasIndex(x => x.PaymentHash);

        // Stored as the member NAME, not the ordinal EF would default to. An ordinal is positional:
        // adding a corridor or a status anywhere but the end silently reinterprets every row already
        // written, turning a `BtcToLightning` swap into a `LightningToBtc` one with no migration, no
        // error, and nothing in the row to say it happened. The name survives any reordering — and a
        // rename, which does break it, is a visible source change rather than an invisible one.
        //
        // It also makes the column readable: a support question about a stuck swap is answered by
        // looking at the row, not by counting enum members in a matching build of the SDK.
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);

        // Corridor-specific state as a JSON string column, the same way ArkWalletEntity carries its
        // Metadata. Provider-agnostic — Postgres jsonb / SQLite TEXT / SQL Server nvarchar(max) —
        // because the value converter does the round trip rather than binding to one database's JSON
        // support. A column per corridor field is what this replaces: eight nullable columns of
        // which any row uses three, and a migration for every corridor added.
        builder.Property(x => x.Metadata)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null)
                      ?? new Dictionary<string, string>())
            // Without a comparer EF compares the dictionary by reference, so a mutation through the
            // typed views would not mark the row dirty and the save would silently write nothing.
            .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, string>>(
                (a, b) => ReferenceEquals(a, b) ||
                          (a != null && b != null && a.Count == b.Count && !a.Except(b).Any()),
                d => d == null ? 0 : d.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                d => new Dictionary<string, string>(d)));
    }
}
