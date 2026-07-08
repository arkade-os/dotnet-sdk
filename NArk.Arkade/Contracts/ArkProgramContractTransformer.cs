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
/// compiled function and resolves its witness at coin-materialization time (<c>SpendingService</c>
/// then spends any <see cref="ArkCoin"/> uniformly). Two entry points: the
/// <see cref="IContractTransformer"/> <see cref="Transform(string, ArkContract, ArkVtxo)"/> auto-selects
/// the single fully-args-bound path, and the explicit
/// <see cref="Transform(string, ArkProgramContract, ArkVtxo, string, IReadOnlyDictionary{string, ArkadeToken})"/>
/// overload spends a named path with call-time witness values (preimage, signature, …).
/// </summary>
/// <remarks>
/// <para>
/// Auto-select scope: a program is auto-transformable only if it has <em>exactly one</em> function whose
/// signers include <c>"user"</c> and whose witness (see <see cref="ArkadeTapscriptSegment.Witness"/>/
/// <see cref="ArkadeCovenantSegment.Witness"/>) resolves fully from the program's constructor
/// <c>args</c> — i.e. no unbound call-time inputs. Programs with several wallet-spendable
/// paths (claim vs. refund), or a path needing call-time values, use the explicit overload to
/// select the path and supply its <c>callArgs</c>.
/// </para>
/// <para>
/// A function with both a <see cref="ArkadeTapscriptSegment.Asm"/> condition <em>and</em> a
/// <see cref="ArkadeFunction.CovenantSegment"/> is also left untransformed: this codebase's
/// <c>ArkCoin.SpendingConditionWitness</c> field is reused by <c>ArkadePsbtExtensions.BuildEmulatorPackets</c>
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

    public Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var programContract = (ArkProgramContract)contract;
        var (compiled, witnessOps) = SelectSpendableFunction(programContract)
            ?? throw new InvalidOperationException("No uniquely spendable function found for this program.");

        return Task.FromResult(BuildCoin(walletIdentifier, programContract, vtxo, compiled, witnessOps));
    }

    /// <summary>
    /// Materializes a specific spending path (<paramref name="functionName"/>) into a spendable
    /// <see cref="ArkCoin"/>, supplying its call-time witness values via <paramref name="callArgs"/>.
    /// Use this when the program exposes several wallet-spendable paths (e.g. claim vs. refund) or a
    /// witness not fully determined by the program's constructor <c>args</c> — the parameterless
    /// <see cref="Transform(string, ArkContract, ArkVtxo)"/> only handles the single, fully-args-bound path.
    /// </summary>
    /// <param name="functionName">Name of the program function (spending path) to spend through.</param>
    /// <param name="callArgs">
    /// Values for the function's declared <c>inputs</c>, keyed by input name. Each is a
    /// <see cref="ArkadeTokenKind.Bytes"/> token (e.g. an HTLC preimage, a counterparty signature,
    /// a pubkey or a hash) or a <see cref="ArkadeTokenKind.Number"/> token (an int) — matching the
    /// input's declared <see cref="ArkadeArgType"/>.
    /// </param>
    /// <exception cref="ArgumentException">No function named <paramref name="functionName"/> exists.</exception>
    /// <exception cref="InvalidOperationException">
    /// The function carries both a covenant and a tapscript condition (unrepresentable with the single
    /// witness field), or a required call argument is missing from <paramref name="callArgs"/>.
    /// </exception>
    public Task<ArkCoin> Transform(
        string walletIdentifier,
        ArkProgramContract contract,
        ArkVtxo vtxo,
        string functionName,
        IReadOnlyDictionary<string, ArkadeToken>? callArgs = null)
    {
        var compiled = contract.FunctionByName(functionName)
            ?? throw new ArgumentException($"Program has no function '{functionName}'.", nameof(functionName));

        var def = compiled.Definition;
        if (def.CovenantSegment is not null && def.Tapscript.Asm is not null)
            throw new InvalidOperationException(
                $"Function '{functionName}' has both a covenant and a tapscript condition; " +
                "the single SpendingConditionWitness field can carry only one.");

        var witnessTokens = def.CovenantSegment?.Witness ?? def.Tapscript.Witness;
        if (!TryResolveWitness(witnessTokens, contract.Args, callArgs ?? EmptyArgs, out var ops))
            throw new InvalidOperationException(
                $"Function '{functionName}' witness could not be resolved — a required call argument is missing.");

        return Task.FromResult(BuildCoin(walletIdentifier, contract, vtxo, compiled, ops));
    }

    private static ArkCoin BuildCoin(
        string walletIdentifier, ArkProgramContract contract, ArkVtxo vtxo,
        CompiledArkadeFunction compiled, IReadOnlyList<Op> witnessOps)
    {
        var witness = witnessOps.Count > 0 ? new WitScript(witnessOps.ToArray()) : null;

        return new ArkCoin(walletIdentifier, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut, contract.User, compiled.ToScriptBuilder(), witness, null, null,
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

            // Auto-select only handles fully-args-bound paths: no call args are supplied, so any
            // witness token referencing a call-time input leaves this function unresolvable here.
            var witnessTokens = def.CovenantSegment?.Witness ?? def.Tapscript.Witness;
            if (!TryResolveWitness(witnessTokens, contract.Args, EmptyArgs, out var ops))
                continue;

            candidates.Add((compiled, ops));
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static readonly IReadOnlyDictionary<string, ArkadeToken> EmptyArgs =
        new Dictionary<string, ArkadeToken>();

    /// <summary>
    /// Resolves a witness token list against the program's constructor <c>args</c> (for
    /// <c>$param</c> tokens) and the spend's <c>callArgs</c> (for bare input-name tokens) —
    /// mirrors the ts-sdk's <c>witnessRefToBytes</c>. Literal byte/number tokens pass through.
    /// Returns <c>false</c> if any token references a value that neither map supplies.
    /// </summary>
    private static bool TryResolveWitness(
        IReadOnlyList<ArkadeToken>? witness,
        IReadOnlyDictionary<string, ArkadeToken> args,
        IReadOnlyDictionary<string, ArkadeToken> callArgs,
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
                // $param → bound at contract construction (constructor args).
                case ArkadeTokenKind.Text when token.IsParam && args.TryGetValue(token.ParamName, out var bound):
                    result.Add(ToPush(bound));
                    continue;
                // bare input name → supplied at spend time (call args): preimage, sig, pubkey, hash, int.
                case ArkadeTokenKind.Text when !token.IsParam && callArgs.TryGetValue(token.Text!, out var callValue):
                    result.Add(ToPush(callValue));
                    continue;
                default:
                    // Unbound $param, or a call-argument name with no supplied value.
                    ops = [];
                    return false;
            }
        }
        ops = result;
        return true;
    }

    private static Op ToPush(ArkadeToken token) => token.Kind switch
    {
        ArkadeTokenKind.Bytes => Op.GetPushOp(token.Bytes!),
        ArkadeTokenKind.Number => ArkadeProgramCompiler.NumberToOp(token.Number!.Value),
        _ => throw new InvalidOperationException("A witness value must be bytes or a number."),
    };
}
