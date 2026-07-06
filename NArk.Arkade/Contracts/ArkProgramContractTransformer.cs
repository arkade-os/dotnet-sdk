using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Program;
using NArk.Core.Transformers;
using NBitcoin;

namespace NArk.Arkade.Contracts;

/// <summary>
/// Materializes an <see cref="ArkProgramContract"/> VTXO into a spendable <see cref="ArkCoin"/>,
/// mirroring <c>HashLockedContractTransformer</c>/<c>DelegateContractTransformer</c>: picks a
/// compiled function and resolves its witness once, at coin-materialization time — this
/// codebase has no per-contract "send" builder (<c>SpendingService</c> spends any
/// <see cref="ArkCoin"/> uniformly), so there is no separate call-time API to build.
/// </summary>
/// <remarks>
/// <para>
/// Scope: a program is transformable only if it has <em>exactly one</em> function whose
/// signers include <c>"user"</c> and whose witness (see <see cref="ArkadeTapscriptSegment.Witness"/>/
/// <see cref="ArkadeCovenantSegment.Witness"/>) resolves fully from the program's constructor
/// <c>args</c> — i.e. no unbound call-time inputs. Programs with several wallet-spendable
/// paths (collaborative vs. unilateral choice) need explicit path-selection context this
/// transformer does not have; they are left untransformed for now.
/// </para>
/// <para>
/// A function with both a <see cref="ArkadeTapscriptSegment.Asm"/> condition <em>and</em> a
/// <see cref="ArkadeFunction.CovenantSegment"/> is also left untransformed: this codebase's
/// <c>ArkCoin.SpendingConditionWitness</c> field is reused by <c>ArkadePsbtExtensions.BuildEmulatorOutput</c>
/// to carry the covenant's witness to the emulator, so it can't simultaneously carry a
/// different witness for the outer tapscript condition.
/// </para>
/// </remarks>
public class ArkProgramContractTransformer(
    IWalletProvider walletProvider,
    ILogger<ArkProgramContractTransformer>? logger = null) : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not ArkProgramContract programContract)
            return false;

        if (programContract.User is null)
            return false;

        if (await walletProvider.GetAddressProviderAsync(walletIdentifier) is not { } addressProvider)
            return false;

        if (!await addressProvider.IsOurs(programContract.User))
        {
            logger?.LogWarning(
                "ArkProgramContract user descriptor not ours: wallet={WalletId}",
                walletIdentifier);
            return false;
        }

        if (await walletProvider.GetSignerAsync(walletIdentifier) is null)
            return false;

        return SelectSpendableFunction(programContract) is not null;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var programContract = (ArkProgramContract)contract;
        var (compiled, witnessOps) = SelectSpendableFunction(programContract)
            ?? throw new InvalidOperationException("No uniquely spendable function found for this program.");

        var witness = witnessOps.Count > 0 ? new WitScript(witnessOps.ToArray()) : null;

        return new ArkCoin(walletIdentifier, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut, programContract.User, compiled.ToScriptBuilder(), witness, null, null,
            vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);
    }

    private static (CompiledArkadeFunction Function, IReadOnlyList<Op> Witness)? SelectSpendableFunction(
        ArkProgramContract contract)
    {
        var candidates = new List<(CompiledArkadeFunction, IReadOnlyList<Op>)>();

        foreach (var compiled in contract.CompiledFunctions)
        {
            var def = compiled.Definition;
            if (!def.Tapscript.Signers.Any(s => s.Kind == ArkadeTokenKind.Text && s.Text == "user"))
                continue;

            // The emulator packet reuses SpendingConditionWitness for the covenant's own
            // witness — a function needing both an outer condition witness and a covenant
            // witness can't be represented with the current single-field design.
            if (def.CovenantSegment is not null && def.Tapscript.Asm is not null)
                continue;

            var witnessTokens = def.CovenantSegment?.Witness ?? def.Tapscript.Witness;
            if (!TryResolveWitness(witnessTokens, contract.Args, out var ops))
                continue;

            candidates.Add((compiled, ops));
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Resolves a witness token list against the program's constructor <c>args</c> only —
    /// mirrors the ts-sdk's <c>witnessRefToBytes</c>, minus the call-time <c>callArgs</c> half
    /// (this transformer has no call-time context, so bare call-argument-name tokens make the
    /// whole list unresolvable).
    /// </summary>
    private static bool TryResolveWitness(
        IReadOnlyList<ArkadeToken>? witness,
        IReadOnlyDictionary<string, ArkadeToken> args,
        out IReadOnlyList<Op> ops)
    {
        var result = new List<Op>();
        foreach (var token in witness ?? [])
        {
            switch (token.Kind)
            {
                case ArkadeTokenKind.Bytes:
                    result.Add(Op.GetPushOp(token.Bytes!));
                    continue;
                case ArkadeTokenKind.Number:
                    result.Add(ArkadeProgramCompiler.NumberToOp(token.Number!.Value));
                    continue;
                case ArkadeTokenKind.Text when token.IsParam && args.TryGetValue(token.ParamName, out var bound):
                    result.Add(bound.Kind == ArkadeTokenKind.Bytes
                        ? Op.GetPushOp(bound.Bytes!)
                        : ArkadeProgramCompiler.NumberToOp(bound.Number!.Value));
                    continue;
                default:
                    // Unbound $param, or a bare name — that's a call-time input this
                    // transformer cannot supply.
                    ops = [];
                    return false;
            }
        }
        ops = result;
        return true;
    }
}
