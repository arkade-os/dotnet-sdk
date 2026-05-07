using NArk.Abstractions.VirtualTxs;

namespace NArk.Core.Transport.Models;

/// <summary>
/// A single entry in a VTXO's virtual transaction chain, from commitment tx to leaf.
/// </summary>
public record VtxoChainEntry(string Txid, DateTimeOffset ExpiresAt, ChainedTxType Type, IReadOnlyList<string> Spends);
