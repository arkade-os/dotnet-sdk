namespace NArk.Abstractions.Contracts;

/// <summary>Persisted representation of an Arkade contract, keyed by its scriptPubKey hex.</summary>
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

    /// <summary>
    /// The effective layer this contract's funds live on (on-chain vs off-chain).
    /// Resolved at write time from the contract's <c>DefaultScope</c> or an explicit override.
    /// </summary>
    public ContractScope Scope { get; init; }
}
