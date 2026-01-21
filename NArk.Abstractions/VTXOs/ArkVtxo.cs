using NArk.Abstractions.Blockchain;
using NBitcoin;

namespace NArk.Abstractions.VTXOs;

public record ArkVtxo(
    string Script,
    string TransactionId,
    uint TransactionOutputIndex,
    ulong Amount,
    string? SpentByTransactionId,
    string? SettledByTransactionId,
    bool Swept,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    uint? ExpiresAtHeight)
{
    public OutPoint OutPoint => new(new uint256(TransactionId), TransactionOutputIndex);
    public TxOut TxOut => new(Money.Satoshis(Amount), NBitcoin.Script.FromHex(Script));


    public ICoinable ToCoin()
    {
        var outpoint = new OutPoint(new uint256(TransactionId), TransactionOutputIndex);
        var txOut = new TxOut(Money.Satoshis(Amount), NBitcoin.Script.FromHex(Script));
        return new Coin(outpoint, txOut);
    }

    public bool IsSpent()
    {
        return !string.IsNullOrEmpty(SpentByTransactionId) || !string.IsNullOrEmpty(SettledByTransactionId);
    }

    private bool IsExpired(TimeHeight current)
    {
        if (ExpiresAt is not null && current.Timestamp >= ExpiresAt)
            return true;
        if (ExpiresAtHeight is not null && current.Height >= ExpiresAtHeight)
            return true;
        return false;
    }

    public bool CanSpendOffchain(TimeHeight current)
    {
        return !IsSpent() && !Swept && !IsExpired(current);
    }

    public bool IsRecoverable()
    {
        return Swept;
    }

    public bool RequiresForfeit()
    {
        return !Swept;
    }
}