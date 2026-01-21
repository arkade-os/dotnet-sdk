namespace NArk.Abstractions.Contracts;

public record ArkContractEntity(
    string Script,
    ContractActivityState ActivityState,
    string Type,
    Dictionary<string, string> AdditionalData,
    string WalletIdentifier,
    DateTimeOffset CreatedAt
);