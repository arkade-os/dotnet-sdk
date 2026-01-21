namespace NArk.Abstractions.Batches;

public record SubmitTreeSignaturesRequest(string BatchId, string PubKey, Dictionary<string, string> TreeSignatures);