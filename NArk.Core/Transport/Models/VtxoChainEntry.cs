namespace NArk.Core.Transport.Models;

/// <summary>
/// A single entry in a VTXO's virtual transaction chain, from commitment tx to leaf.
/// </summary>
public record VtxoChainEntry(string Txid, DateTimeOffset ExpiresAt, ChainedTxType Type, IReadOnlyList<string> Spends);

/// <summary>
/// Type of transaction in a VTXO chain.
/// </summary>
public enum ChainedTxType
{
    Unspecified = 0,
    Commitment = 1,
    Ark = 2,
    Tree = 3,
    Checkpoint = 4
}
