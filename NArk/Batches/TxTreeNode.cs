using NBitcoin;

namespace NArk.Batches;

/// <summary>
/// Node in the transaction tree for serialization
/// </summary>
public record TxTreeNode(PSBT Tx, Dictionary<int, uint256> Children);