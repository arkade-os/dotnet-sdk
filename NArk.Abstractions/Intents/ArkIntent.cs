using NBitcoin;

namespace NArk.Abstractions.Intents;

/// <summary>
/// A submitted Arkade intent: a signed commitment to spend specific VTXOs to given outputs,
/// tracked through the batch lifecycle until it succeeds, fails, or is cancelled.
/// </summary>
public record ArkIntent(
    string IntentTxId,
    string? IntentId,
    string WalletId,
    ArkIntentState State,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RegisterProof,
    string RegisterProofMessage,
    string DeleteProof,
    string DeleteProofMessage,
    string? BatchId,
    string? CommitmentTransactionId,
    string? CancellationReason,
    OutPoint[] IntentVtxos,
    string SignerDescriptor
)
{
#pragma warning disable CS1591
    private sealed class IntentTxIdEqualityComparer : IEqualityComparer<ArkIntent>
    {
        public bool Equals(ArkIntent? x, ArkIntent? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null) return false;
            if (y is null) return false;
            if (x.GetType() != y.GetType()) return false;
            return x.IntentTxId.Equals(y.IntentTxId);
        }

        public int GetHashCode(ArkIntent obj)
        {
            return obj.IntentTxId.GetHashCode();
        }
    }
#pragma warning restore CS1591

    /// <summary>Compares intents by <see cref="IntentTxId"/> for use in collections and dictionaries.</summary>
    public static IEqualityComparer<ArkIntent> IntentTxIdComparer { get; } = new IntentTxIdEqualityComparer();
}
