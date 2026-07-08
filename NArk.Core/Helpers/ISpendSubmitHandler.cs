using NArk.Abstractions;
using NBitcoin;

namespace NArk.Core.Helpers;

/// <summary>
/// Plug-point that lets a higher layer (e.g. <c>NArk.Arkade</c>) take over the
/// submission of an offchain Ark transaction, without <c>NArk.Core</c> depending
/// on that layer. Used for covenant spends, where the emulator co-signing service
/// — not arkd directly — is the submission endpoint (it co-signs and fronts arkd).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ShouldHandle"/> is a cheap per-spend gate (typically "is any input
/// arkade-bound?"). When it returns <c>true</c>, <c>TransactionHelpers</c> user-signs
/// the checkpoint inputs and hands the fully user-signed ark tx + checkpoints to
/// <see cref="SubmitAsync"/>, which owns the rest (co-sign + submit + finalize); the
/// normal arkd cooperative submit is skipped. When no registered handler engages,
/// the spend follows the unchanged arkd flow.
/// </para>
/// </remarks>
public interface ISpendSubmitHandler
{
    /// <summary>Cheap check: does this handler own submission for this spend?</summary>
    bool ShouldHandle(IReadOnlyCollection<ArkCoin> coins);

    /// <summary>
    /// Fully submit the spend: co-sign, forward to arkd and finalize. Called only
    /// when <see cref="ShouldHandle"/> returned <c>true</c>. The ark tx and every
    /// checkpoint already carry the wallet's own signatures.
    /// </summary>
    /// <param name="coins">The spend inputs, in transaction-input order.</param>
    /// <param name="arkTx">The user-signed ark transaction.</param>
    /// <param name="checkpoints">The user-signed checkpoint transactions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SubmitAsync(
        IReadOnlyCollection<ArkCoin> coins,
        PSBT arkTx,
        IReadOnlyList<PSBT> checkpoints,
        CancellationToken cancellationToken);
}
