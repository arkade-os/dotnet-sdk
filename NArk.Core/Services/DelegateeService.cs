using System.Text.Json;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Models;
using NArk.Core.Models.Options;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Core.Services;

/// <summary>
/// Thrown when a delegation request fails validation and must be rejected.
/// </summary>
public sealed class DelegationRejectedException(string message) : Exception(message);

/// <summary>
/// Server-side handler for the Arkade VTXO-refresh delegation service (the delegatee). Implements the
/// behaviour behind <c>fulmine.v1.DelegatorService</c>: advertises the delegator's identity and fee,
/// accepts delegations, co-signs the forfeit's delegate path with the delegator's signer, and writes
/// them into the intent pipeline for refresh before expiry.
/// </summary>
public class DelegateeService(
    IWalletProvider walletProvider,
    IClientTransport clientTransport,
    IIntentStorage intentStorage,
    IContractStorage contractStorage,
    IVtxoStorage vtxoStorage,
    IOptions<DelegatorOptions> options)
{
    private readonly DelegatorOptions _options = options.Value;

    /// <summary>
    /// Returns the delegator's public key (hex), advertised fee, and fee address. The pubkey is what
    /// clients embed in the delegate leaf of their delegate contract.
    /// </summary>
    public async Task<DelegatorInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var signer = await Signer(cancellationToken);
        var pubkey = await signer.GetPubKey(_options.DelegateDescriptor, cancellationToken);
        var pubkeyHex = Convert.ToHexString(pubkey.ToBytes()).ToLowerInvariant();
        return new DelegatorInfo(pubkeyHex, _options.Fee, _options.DelegatorAddress);
    }

    /// <summary>
    /// Accepts a delegation: validates the intent proof + forfeit txs, co-signs each forfeit's delegate
    /// leaf with the delegator's signer, and writes an Arkade intent into the pipeline with a future
    /// <see cref="ArkIntent.ValidFrom"/> so it is refreshed shortly before the underlying VTXOs expire.
    /// </summary>
    /// <param name="intentMessage">The intent message in plain-text (stringified JSON).</param>
    /// <param name="intentProof">The intent proof tx (PSBT in base64 format).</param>
    /// <param name="forfeitTxs">Partially-signed forfeit transactions (base64), one per VTXO.</param>
    /// <param name="rejectReplace">If true, reject when an overlapping active delegation already exists.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AcceptAsync(
        string intentMessage, string intentProof, string[] forfeitTxs,
        bool rejectReplace, CancellationToken cancellationToken = default)
    {
        if (forfeitTxs.Length == 0)
            throw new DelegationRejectedException("at least one forfeit tx is required");

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var network = serverInfo.Network;

        PSBT intentPsbt;
        try { intentPsbt = PSBT.Parse(intentProof, network); }
        catch { throw new DelegationRejectedException("intent proof is not a valid PSBT"); }

        var signer = await Signer(cancellationToken);
        var delegateXOnly = (await signer.GetPubKey(_options.DelegateDescriptor, cancellationToken)).ToXOnlyPubKey();
        var serverXOnly = serverInfo.SignerKey.ToXOnlyPubKey();

        // Co-sign each forfeit's delegate leaf and collect the referenced VTXO outpoints.
        var outpoints = new List<OutPoint>();
        var cosignedForfeits = new List<string>();
        foreach (var forfeitB64 in forfeitTxs)
        {
            PSBT forfeitPsbt;
            try { forfeitPsbt = PSBT.Parse(forfeitB64, network); }
            catch { throw new DelegationRejectedException("forfeit tx is not a valid PSBT"); }

            var input = forfeitPsbt.Inputs[0];
            var vtxoTxOut = input.GetTxOut()
                ?? throw new DelegationRejectedException("forfeit input is missing its witness UTXO");
            var outpoint = input.PrevOut;

            var contract = ReconstructDelegateContract(forfeitPsbt, serverInfo, delegateXOnly, serverXOnly, network);

            // Parity check: the reconstructed contract must produce exactly the VTXO being spent.
            if (contract.GetScriptPubKey() != vtxoTxOut.ScriptPubKey)
                throw new DelegationRejectedException("forfeit does not reference a delegate contract bearing this delegator's key");

            var forfeitCoin = new ArkCoin(
                _options.WalletId, contract, DateTimeOffset.UtcNow, null, null,
                outpoint, vtxoTxOut, _options.DelegateDescriptor, contract.DelegatePath(),
                null, null, null, swept: false, unrolled: false);

            var precomputed = forfeitPsbt.GetGlobalTransaction().PrecomputeTransactionData([vtxoTxOut]);
            await PsbtHelpers.SignAndFillPsbt(signer, forfeitCoin, forfeitPsbt, precomputed,
                TaprootSigHash.All | TaprootSigHash.AnyoneCanPay, cancellationToken);

            outpoints.Add(outpoint);
            cosignedForfeits.Add(forfeitPsbt.ToBase64());
        }

        ValidateFee(intentPsbt, network);

        // reject_replace / supersede any active intent overlapping these VTXOs.
        var overlapping = await intentStorage.GetIntents(
            containingInputs: outpoints.ToArray(),
            states: [ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch, ArkIntentState.BatchInProgress],
            cancellationToken: cancellationToken);
        if (overlapping.Count > 0)
        {
            if (rejectReplace)
                throw new DelegationRejectedException("an overlapping delegation already exists and reject_replace is set");
            foreach (var o in overlapping)
                await intentStorage.SaveIntent(o.WalletId, o with
                {
                    State = ArkIntentState.Cancelled,
                    CancellationReason = "Superseded by a new delegation",
                    UpdatedAt = DateTimeOffset.UtcNow
                }, cancellationToken);
        }

        var (validFrom, validUntil) = await ComputeRefreshWindowAsync(outpoints, cancellationToken);

        var intentTxId = intentPsbt.GetGlobalTransaction().GetHash().ToString();
        await intentStorage.SaveIntent(_options.WalletId, new ArkIntent(
            intentTxId, null, _options.WalletId, ArkIntentState.WaitingToSubmit,
            validFrom, validUntil, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            intentProof, intentMessage, /*DeleteProof*/ "", /*DeleteProofMessage*/ "",
            null, null, null, outpoints.ToArray(), _options.DelegateDescriptor.ToString())
        {
            PartialForfeits = cosignedForfeits.ToArray()
        }, cancellationToken);
    }

    /// <summary>
    /// Rebuilds the delegate contract from the forfeit's delegate leaf. The leaf carries three x-only
    /// keys (user, delegate, server); the user key is the one that is neither this delegator's key nor
    /// the Arkade server key. Reconstruction is deterministic and parity-safe (the taproot tree uses a
    /// fixed unspendable internal key and x-only leaf keys), and is verified by the caller against the
    /// VTXO scriptPubKey.
    /// </summary>
    private ArkDelegateContract ReconstructDelegateContract(
        PSBT forfeitPsbt, ArkServerInfo serverInfo, ECXOnlyPubKey delegateXOnly, ECXOnlyPubKey serverXOnly, Network network)
    {
        var leafScript = ReadSpendingLeafScript(forfeitPsbt.Inputs[0])
            ?? throw new DelegationRejectedException("forfeit input is missing its tapscript leaf");

        var keys = ExtractXOnlyKeys(leafScript);
        var comparer = ECXOnlyPubKeyComparer.Instance;
        var userKey = keys.FirstOrDefault(k => !comparer.Equals(k, delegateXOnly) && !comparer.Equals(k, serverXOnly))
            ?? throw new DelegationRejectedException("forfeit delegate leaf does not contain a distinct user key");
        if (!keys.Any(k => comparer.Equals(k, delegateXOnly)))
            throw new DelegationRejectedException("forfeit delegate leaf does not carry this delegator's key");

        var userDescriptor = KeyExtensions.ParseOutputDescriptor(
            Convert.ToHexString(userKey.ToBytes()).ToLowerInvariant(), network);

        return new ArkDelegateContract(
            serverInfo.SignerKey, serverInfo.UnilateralExit, userDescriptor, _options.DelegateDescriptor);
    }

    /// <summary>Reads the spending tapscript leaf (PSBT_IN_TAP_LEAF_SCRIPT, key type 0x15) from a PSBT input.</summary>
    private static Script? ReadSpendingLeafScript(PSBTInput input)
    {
        foreach (var (key, value) in input.Unknown)
        {
            // key = [0x15, <control block>]; value = [<script bytes>, <leaf version byte>].
            if (key.Length > 0 && key[0] == 0x15 && value.Length >= 1)
                return new Script(value[..^1]);
        }
        return null;
    }

    private static List<ECXOnlyPubKey> ExtractXOnlyKeys(Script script)
    {
        var keys = new List<ECXOnlyPubKey>();
        foreach (var op in script.ToOps())
            if (op.PushData is { Length: 32 } push)
                keys.Add(ECXOnlyPubKey.Create(push));
        return keys;
    }

    private void ValidateFee(PSBT intentPsbt, Network network)
    {
        if (!ulong.TryParse(_options.Fee, out var fee) || fee == 0)
            return;
        if (string.IsNullOrEmpty(_options.DelegatorAddress))
            throw new DelegationRejectedException("delegator fee is configured but no fee address is set");

        var feeScript = ArkAddress.Parse(_options.DelegatorAddress).ScriptPubKey;
        var paid = (ulong)intentPsbt.GetGlobalTransaction().Outputs
            .Where(o => o.ScriptPubKey == feeScript)
            .Sum(o => o.Value.Satoshi);
        if (paid < fee)
            throw new DelegationRejectedException($"delegation does not pay the {fee}-sat service fee to the delegator");
    }

    private async Task<(DateTimeOffset? validFrom, DateTimeOffset? validUntil)> ComputeRefreshWindowAsync(
        IReadOnlyCollection<OutPoint> outpoints, CancellationToken cancellationToken)
    {
        DateTimeOffset? earliestExpiry = null;
        await foreach (var vtxo in clientTransport.GetVtxosByOutpoints(outpoints, spentOnly: false, cancellationToken))
            if (vtxo.ExpiresAt is { } e && (earliestExpiry is null || e < earliestExpiry))
                earliestExpiry = e;

        if (earliestExpiry is null)
            return (DateTimeOffset.UtcNow, null);

        return (earliestExpiry.Value - _options.RefreshThreshold, earliestExpiry);
    }

    private async Task<IArkadeWalletSigner> Signer(CancellationToken cancellationToken) =>
        await walletProvider.GetSignerAsync(_options.WalletId, cancellationToken)
        ?? throw new InvalidOperationException($"No signer for delegator wallet {_options.WalletId}");
}
