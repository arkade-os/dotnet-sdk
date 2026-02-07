namespace NArk.Abstractions.Contracts;

public record ArkContractEntity(
    string Script,
    ContractActivityState ActivityState,
    string Type,
    Dictionary<string, string> AdditionalData,
    string WalletIdentifier,
    DateTimeOffset CreatedAt
)
{
    /// <summary>
    /// Application-level metadata (e.g., source tracking).
    /// Separate from AdditionalData which stores contract crypto params.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}