using NArk.Abstractions.Blockchain;
using NBitcoin;

namespace NArk.Abstractions.VTXOs;

/// <summary>
/// A VTXO stored by the SDK: an off-chain tree output, a boarding UTXO, or an unrolled on-chain output.
/// </summary>
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
    uint? ExpiresAtHeight,
    bool Preconfirmed = false,
    bool Unrolled = false,
    IReadOnlyList<string>? CommitmentTxids = null,
    string? ArkTxid = null,
    IReadOnlyList<VtxoAsset>? Assets = null,
    Dictionary<string, string>? Metadata = null)
{
    /// <summary>
    /// Metadata key carrying the on-chain confirmation state of an unrolled
    /// (on-chain) VTXO, as <c>bool.ToString()</c> ("True"/"False"). Populated
    /// by the boarding-UTXO sync from the explorer's confirmation status. The
    /// writer (BoardingUtxoSyncService) and all readers share this constant.
    /// </summary>
    public const string ConfirmedMetadataKey = "Confirmed";

    /// <summary>Outpoint identifying this VTXO (txid + vout).</summary>
    public OutPoint OutPoint => new(new uint256(TransactionId), TransactionOutputIndex);
    /// <summary>The transaction output (amount + scriptPubKey) for PSBT construction.</summary>
    public TxOut TxOut => new(Money.Satoshis(Amount), NBitcoin.Script.FromHex(Script));


    /// <summary>Wraps this VTXO as an NBitcoin <see cref="ICoinable"/> for use in PSBT construction.</summary>
    public ICoinable ToCoin()
    {
        var outpoint = new OutPoint(new uint256(TransactionId), TransactionOutputIndex);
        var txOut = new TxOut(Money.Satoshis(Amount), NBitcoin.Script.FromHex(Script));
        return new Coin(outpoint, txOut);
    }

    /// <summary>True if this VTXO has been spent offchain or settled on-chain.</summary>
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

    /// <summary>True when the VTXO is unspent and not yet recoverable (can participate in an offchain intent).</summary>
    public bool CanSpendOffchain(TimeHeight current)
    {
        // VTXOs can be spent offchain (in Ark protocol) if they are NOT spent and NOT recoverable.
        // Recoverable VTXOs are swept or expired and can only be redeemed onchain.
        return !IsSpent() && !IsRecoverable(current);
    }

    /// <summary>True when the VTXO can be redeemed on-chain (swept or past expiry).</summary>
    public bool IsRecoverable(TimeHeight current)
    {
        return Swept || IsExpired(current) ;
    }

    /// <summary>
    /// True when this VTXO is an on-chain output whose funding transaction is
    /// not yet confirmed, and therefore cannot be spent or settled. Reads the
    /// <see cref="ConfirmedMetadataKey"/> flag: a value other than "True"
    /// (case-insensitive) means unconfirmed.
    /// <para>
    /// Today only boarding UTXOs carry this flag (set by the boarding-UTXO
    /// sync from the explorer). VTXOs without the key — off-chain tree VTXOs
    /// and arkd-reported unrolled VTXOs, which the indexer does not surface
    /// confirmation data for — return <c>false</c>. If confirmation data is
    /// later wired for arkd-reported unrolled VTXOs, setting the same flag
    /// makes this (and every consumer) generalise without further changes.
    /// </para>
    /// </summary>
    public bool IsUnconfirmedOnchain()
    {
        return Metadata is not null
               && Metadata.TryGetValue(ConfirmedMetadataKey, out var confirmed)
               && !string.Equals(confirmed, bool.TrueString, StringComparison.OrdinalIgnoreCase);
    }
    //
    // public bool RequiresForfeit()
    // {
    //     return !Swept;
    // }
}