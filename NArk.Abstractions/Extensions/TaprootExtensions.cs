using NBitcoin;

namespace NArk.Abstractions.Extensions;

public static class TaprootExtensions
{
    /// <summary>
	/// Builds a balanced binary tree from TapScript leaves, similar to AssembleTaprootScriptTree from Bitcoin Core.
	/// Pairs leaves sequentially instead of using weight-based pairing.
	/// </summary>
	/// <param name="leaves">The TapScript leaves to include in the tree</param>
	/// <returns>The root TaprootNodeInfo of the constructed tree</returns>
	public static TaprootNodeInfo BuildTree(this TapScript[] leaves)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        switch (leaves.Length)
        {
            case 0:
                throw new ArgumentException("Leaves has 0 length.", nameof(leaves));
            case 1:
                return TaprootNodeInfo.NewLeaf(leaves[0]);
        }

        // Create initial branches by pairing sequential leaves
        var branches = new List<TaprootNodeInfo?>();
        for (var i = 0; i < leaves.Length; i += 2)
        {
            // If there's only a single leaf left, then we'll merge this
            // with the last branch we have.
            if (i == leaves.Length - 1)
            {
                if (branches[^1] is { } nodeInfo)
                    branches[^1] = nodeInfo + TaprootNodeInfo.NewLeaf(leaves[i]);
                else
                    throw new Exception("This should never happen");

                continue;
            }
            // While we still have leaves left, we'll combine two of them
            // into a new branch node.
            branches.Add(TaprootNodeInfo.NewLeaf(leaves[i]) + TaprootNodeInfo.NewLeaf(leaves[i + 1]));
        }

        // Merge all the leaf branches one by one until we have the final root.
        while (branches.Count != 0)
        {
            if (branches.Count == 1)
                return branches.Single()!;

            var left = branches[0]!;
            var right = branches[1]!;
            branches = [.. branches[2..], left + right];
        }

        throw new InvalidOperationException("This should never happen");
    }
}
