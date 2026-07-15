using NArk.Abstractions.VirtualTxs;

namespace NArk.Core.Transport.Models;

/// <summary>
/// A single entry in a VTXO's virtual transaction chain. Chains returned by
/// <see cref="NArk.Core.Transport.IClientTransport.GetVtxoChainAsync"/> are
/// ordered leaf -> root: the VTXO's own tx first, ending at the on-chain
/// Commitment anchor.
/// </summary>
public record VtxoChainEntry(string Txid, DateTimeOffset ExpiresAt, ChainedTxType Type, IReadOnlyList<string> Spends);
