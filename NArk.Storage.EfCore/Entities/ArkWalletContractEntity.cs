using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NArk.Abstractions.Contracts;

namespace NArk.Storage.EfCore.Entities;

public class ArkWalletContractEntity
{
    public string Script { get; set; } = "";

    public ContractActivityState ActivityState { get; set; } = ContractActivityState.Inactive;
    public string Type { get; set; } = "";

    /// <summary>
    /// The effective layer this contract's funds live on (on-chain vs off-chain).
    /// Indexed so sync/recovery/sweep can filter by scope in SQL. Defaults to
    /// <see cref="ContractScope.Offchain"/> for rows written before this column existed.
    /// </summary>
    public ContractScope Scope { get; set; } = ContractScope.Offchain;

    [Column("ContractData", TypeName = "jsonb")]
    public string ContractDataJson { get; set; } = "{}";

    [Column("Metadata", TypeName = "jsonb")]
    public string? MetadataJson { get; set; }

    [NotMapped]
    public Dictionary<string, string> ContractData
    {
        get => JsonSerializer.Deserialize<Dictionary<string, string>>(ContractDataJson) ?? new();
        set => ContractDataJson = JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public Dictionary<string, string>? Metadata
    {
        get => MetadataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson);
        set => MetadataJson = value is null ? null : JsonSerializer.Serialize(value);
    }

    public string WalletId { get; set; } = "";
    public ArkWalletEntity Wallet { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ArkSwapEntity> Swaps { get; set; } = [];

    internal static void Configure(EntityTypeBuilder<ArkWalletContractEntity> builder, ArkStorageOptions options)
    {
        builder.ToTable(options.WalletContractsTable, options.Schema);
        builder.HasKey(w => new { w.Script, w.WalletId });

        builder.HasIndex(w => w.Scope);

        builder.HasOne(w => w.Wallet)
            .WithMany(w => w.Contracts)
            .HasForeignKey(w => w.WalletId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
