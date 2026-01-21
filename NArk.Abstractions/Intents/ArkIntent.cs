using NBitcoin;

namespace NArk.Abstractions.Intents;

public record ArkIntent(
    Guid InternalId,
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
    private sealed class InternalIdEqualityComparer : IEqualityComparer<ArkIntent>
    {
        public bool Equals(ArkIntent? x, ArkIntent? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null) return false;
            if (y is null) return false;
            if (x.GetType() != y.GetType()) return false;
            return x.InternalId.Equals(y.InternalId);
        }

        public int GetHashCode(ArkIntent obj)
        {
            return obj.InternalId.GetHashCode();
        }
    }

    public static IEqualityComparer<ArkIntent> InternalIdComparer { get; } = new InternalIdEqualityComparer();
}