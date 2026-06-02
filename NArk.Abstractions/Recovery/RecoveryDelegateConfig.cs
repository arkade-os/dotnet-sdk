using NBitcoin.Scripting;

namespace NArk.Abstractions.Recovery;

/// <summary>
/// Optional delegate (auto-renewal) public-key descriptors a wallet may have used.
/// When non-empty, recovery also derives the delegate-vtxo variant per index/signer
/// — mirroring the canonical <c>arkade-os/ts-sdk</c> restore, which derives both
/// <c>DefaultVtxo</c> and <c>DelegateVtxo</c> scripts — so funds locked under a
/// delegate script are discovered too. Empty by default (no delegation configured).
/// </summary>
public sealed class RecoveryDelegateConfig
{
    /// <summary>Delegate key descriptors to additionally probe during recovery.</summary>
    public IReadOnlyList<OutputDescriptor> Delegates { get; init; } = [];
}
