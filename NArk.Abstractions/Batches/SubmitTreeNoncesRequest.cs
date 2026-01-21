namespace NArk.Abstractions.Batches;

public record SubmitTreeNoncesRequest(string BatchId, string PubKey, Dictionary<string, string> Nonces);