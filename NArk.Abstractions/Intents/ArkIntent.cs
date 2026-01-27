using NBitcoin;

namespace NArk.Abstractions.Intents;

public record ArkIntent(
    string IntentTxId,
    string? IntentId,
    string WalletId,
    ArkIntentState State,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
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

    public static IEqualityComparer<ArkIntent> IntentTxIdComparer { get; } = new IntentTxIdEqualityComparer();
}