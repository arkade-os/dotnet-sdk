namespace NArk.Core.Transport.Models;

/// <summary>
/// A node in the VTXO tree structure returned by the indexer.
/// </summary>
public record VtxoTreeNode(string Txid, Dictionary<uint, string> Children);
