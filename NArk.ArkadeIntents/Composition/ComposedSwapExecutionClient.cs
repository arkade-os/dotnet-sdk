using System.Globalization;
using System.Numerics;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Services;
using NArk.Core.Transport;

namespace NArk.ArkadeIntents.Composition;

/// <summary>Public progress only; ingress completion is not merchant settlement.</summary>
/// <param name="OutgoingSwapId">Independent Arkade-to-EVM RFQ identity.</param>
/// <param name="OutgoingStatus">Observed or verified outgoing lifecycle.</param>
/// <param name="IngressSwapId">Optional linked receive RFQ identity.</param>
/// <param name="IngressStatus">Ingress lifecycle, never an EVM delivery signal.</param>
/// <param name="EvmClaimTxid">Transaction proving exact ERC20 delivery, or null.</param>
/// <param name="LockProof">Durable depth/age proof preceding claim submission, or null.</param>
/// <param name="DeliveredAmount">Receipt-verified ERC20 atomic amount as canonical decimal, or null.</param>
public sealed record ComposedSwapExecutionResult(string OutgoingSwapId, ArkadeSwapIntentStatus OutgoingStatus,
    string? IngressSwapId, ArkadeSwapIntentStatus? IngressStatus, string? EvmClaimTxid,
    EvmLockProof? LockProof = null, string? DeliveredAmount = null)
{
    /// <summary>Latest ingress refund failure or incomplete outcome, or null.</summary>
    public string? IngressError { get; init; }
}

/// <summary>Advances persisted composed routes without disclosing their SDK-held secrets in results.</summary>
/// <remarks>
/// The internal gate serializes only calls made through this executor instance. A host running
/// multiple instances or processes must take a distributed lock keyed by the outgoing swap id;
/// intent storage does not expose compare-and-swap.
/// </remarks>
public sealed class ComposedSwapExecutionClient
{
    private readonly IArkadeIntentStorage _storage;
    private readonly IVtxoStorage _vtxos;
    private readonly IBitcoinBlockchain _blockchain;
    private readonly IContractStorage _contracts;
    private readonly IClientTransport _transport;
    private readonly LightningIntentsClient _lightning;
    private readonly OnchainIntentsClient? _onchain;
    private readonly EvmSwapChainClient _chain;
    private readonly EvmSendPolicy _policy;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates an executor using existing NI clients and host-controlled EVM proof and durable signing adapters.</summary>
    /// <param name="storage">SDK recovery storage containing the intents and their secrets.</param>
    /// <param name="vtxos">Synchronized VTXOs used to prove exact funding of the outgoing Arkade lock.</param>
    /// <param name="blockchain">Bitcoin chain time used to exclude expired Arkade funding.</param>
    /// <param name="contracts">Imported covenant descriptors.</param>
    /// <param name="transport">Arkade network facts.</param>
    /// <param name="lightning">NI Lightning receive and outgoing refund implementation.</param>
    /// <param name="onchain">NI onchain receive implementation, optional for other rails.</param>
    /// <param name="rpc">Configured EVM chain reader.</param>
    /// <param name="sender">Host sender that saves a deterministic transaction identity before broadcast.</param>
    /// <param name="policy">Expected chain, token, contract and proof bounds.</param>
    /// <param name="timeProvider">Clock used to enforce safety windows.</param>
    public ComposedSwapExecutionClient(IArkadeIntentStorage storage, IVtxoStorage vtxos,
        IBitcoinBlockchain blockchain, IContractStorage contracts,
        IClientTransport transport, LightningIntentsClient lightning, OnchainIntentsClient? onchain,
        IEvmSwapRpc rpc, IEvmDurableTransactionSender sender, EvmSendPolicy policy, TimeProvider? timeProvider = null)
    {
        _storage = storage;
        _vtxos = vtxos;
        _blockchain = blockchain;
        _contracts = contracts;
        _transport = transport;
        _lightning = lightning;
        _onchain = onchain;
        _policy = policy;
        _time = timeProvider ?? TimeProvider.System;
        _chain = new EvmSwapChainClient(rpc, sender, policy, _time);
    }

    /// <summary>Advances one route; exact funding can trigger a pending EVM leg and verified completion is repeatable.</summary>
    /// <param name="outgoingSwapId">Persisted BtcToEvm RFQ id.</param>
    /// <param name="ingressSwapId">Persisted Lightning/onchain receive RFQ id, or null for direct Arkade.</param>
    /// <param name="cancellationToken">Cancels reads or submission; prepared EVM identity survives an uncertain broadcast.</param>
    /// <returns>Public lifecycle and verified transaction identity only.</returns>
    /// <remarks>Callers must serialize this route across executor instances and processes.</remarks>
    public async Task<ComposedSwapExecutionResult> AdvanceAsync(string outgoingSwapId, string? ingressSwapId = null,
        CancellationToken cancellationToken = default)
    {
        ComposedRouteExecutionGuard.RequireId(outgoingSwapId);
        if (ingressSwapId is not null)
        {
            ComposedRouteExecutionGuard.RequireId(ingressSwapId);
            if (ingressSwapId == outgoingSwapId)
                throw new InvalidOperationException("composed RFQ identities must be independent");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var outgoing = await LoadAsync(outgoingSwapId, cancellationToken);
            if (outgoing.Type != ArkadeSwapIntentType.BtcToEvm || outgoing.OfferAmount.Satoshi <= 0)
                throw new InvalidOperationException("composed outgoing intent is not a positive Arkade-to-EVM quote");
            var ingress = ingressSwapId is null ? null : await LoadAsync(ingressSwapId, cancellationToken);
            ValidateIdentity(outgoing, ingress);
            outgoing = await ReconcileAsync(outgoing, cancellationToken);
            if (ingress is not null) ingress = await ReconcileAsync(ingress, cancellationToken);
            var submitted = outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid);
            var preparedRaw = outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction);
            string? ingressError = null;
            if (outgoing.Status == ArkadeSwapIntentStatus.Pending && ingress?.Status == ArkadeSwapIntentStatus.Claimable)
            {
                Values(outgoing);
                var server = await _transport.GetServerInfoAsync(cancellationToken);
                var contract = await LightningCorridor.LoadLockupAsync(_contracts, ingress.SwapPkScript,
                    ingress.Id, server.Network, cancellationToken);
                await ComposedRouteExecutionGuard.ValidateIngressAsync(
                    _storage, _contracts, ingress, contract, server.Network, _time.GetUtcNow().ToUnixTimeSeconds(), cancellationToken);
                // M-to-L reveals P so the solver can create the EVM lock; its fixed claim address keeps any later claim pinned to this recipient.
                ingress = ingress.Type == ArkadeSwapIntentType.LightningToBtc
                    ? await _lightning.ClaimNonInteractiveAsync(ingress.Id, cancellationToken)
                    : await (_onchain ?? throw new InvalidOperationException("onchain ingress client is unavailable"))
                        .ClaimNonInteractiveAsync(ingress.Id, cancellationToken);
                outgoing = await LoadAsync(outgoing.Id, cancellationToken);
            }
            if (ingress is not null
                && ArkadeIntentPolicy.NextAction(ingress) == ArkadeIntentAction.RefundOnchain)
            {
                try
                {
                    var refund = await (_onchain
                        ?? throw new InvalidOperationException("onchain ingress client is unavailable"))
                        .RefundOnchainReceiveAsync(ingress.Id, cancellationToken: cancellationToken);
                    ingressError = refund.Refunded ? null : refund.Detail;
                    ingress = await LoadAsync(ingress.Id, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ingressError = ex.Message;
                }
            }
            if (outgoing.Status == ArkadeSwapIntentStatus.Fulfilled)
            {
                if (!outgoing.Metadata.ContainsKey(ArkadeSwapMetadataKeys.EvmClaimTxid))
                    throw new InvalidOperationException("completed EVM route has no verified claim transaction");
                return Result(outgoing, ingress, ingressError);
            }
            if (outgoing.Status == ArkadeSwapIntentStatus.Refundable && submitted is null)
                return Result(await _lightning.RefundNonInteractiveAsync(outgoing.Id, cancellationToken), ingress,
                    ingressError);

            if (outgoing.Status == ArkadeSwapIntentStatus.Pending && submitted is null
                && !await HasExactOutgoingFundingAsync(outgoing, cancellationToken))
                return Result(outgoing, ingress, ingressError);
            if (submitted is null
                && outgoing.Status is not (ArkadeSwapIntentStatus.Pending or ArkadeSwapIntentStatus.Claimable))
                return Result(outgoing, ingress, ingressError);
            if (outgoing.Status == ArkadeSwapIntentStatus.Claimable && string.IsNullOrEmpty(outgoing.SpentTxid))
                throw new InvalidOperationException("outgoing Claimable requires a proven Arkade spend");
            var executionStatus = outgoing.Status;
            var values = Values(outgoing);
            var preimage = ComposedRouteExecutionGuard.ValidateSecret(outgoing);
            if (submitted is null)
            {
                // Persist the depth/age proof for recovery diagnostics. ClaimForAsync deliberately
                // proves again and re-reads latest state immediately before signing; this snapshot
                // is evidence, not authorization to submit later against a changed lock.
                var proof = await _chain.ProveLockAsync(values, cancellationToken);
                outgoing.Metadata[ArkadeSwapMetadataKeys.EvmLockObservedAtBlock] = proof.ObservedAtBlock.ToString(CultureInfo.InvariantCulture);
                outgoing.Metadata[ArkadeSwapMetadataKeys.EvmLockProvenAtBlock] = proof.ProvenAtBlock.ToString(CultureInfo.InvariantCulture);
                outgoing.Metadata[ArkadeSwapMetadataKeys.EvmLockProvenBlockTimestamp] = proof.ProvenBlockTimestamp.ToString(CultureInfo.InvariantCulture);
                await _storage.SaveArkadeSwapIntent(outgoing, cancellationToken);
            }
            var verified = submitted is not null
                ? preparedRaw is null
                    ? await _chain.VerifyClaimAsync(submitted, values, preimage, cancellationToken)
                    : await _chain.ResumeClaimAsync(
                        new EvmPreparedTransaction(submitted, preparedRaw), values, preimage,
                        outgoing.RefundLocktime, cancellationToken)
                : await _chain.ClaimForAsync(values, preimage,
                    (txid, token) => SavePreparedAsync(outgoing.Id, values, executionStatus, txid, token),
                    outgoing.RefundLocktime, cancellationToken);
            outgoing = await LoadAsync(outgoing.Id, cancellationToken);
            if (Values(outgoing) != values
                || submitted is null
                && outgoing.Status is not (ArkadeSwapIntentStatus.Pending or ArkadeSwapIntentStatus.Claimable))
                throw new InvalidOperationException("outgoing route changed during EVM claim verification");
            var verifiedStatus = outgoing.Status;
            var previous = outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimTxid);
            var previousAmount = outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmDeliveredAmount);
            outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimTxid] = verified.TransactionHash;
            outgoing.Metadata[ArkadeSwapMetadataKeys.EvmDeliveredAmount] = verified.DeliveredAmount.ToString(CultureInfo.InvariantCulture);
            outgoing.Status = ArkadeSwapIntentStatus.Fulfilled;
            try
            {
                await _storage.SaveArkadeSwapIntent(outgoing, cancellationToken);
            }
            catch
            {
                outgoing.Status = verifiedStatus;
                if (previous is null) outgoing.Metadata.Remove(ArkadeSwapMetadataKeys.EvmClaimTxid);
                else outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimTxid] = previous;
                if (previousAmount is null) outgoing.Metadata.Remove(ArkadeSwapMetadataKeys.EvmDeliveredAmount);
                else outgoing.Metadata[ArkadeSwapMetadataKeys.EvmDeliveredAmount] = previousAmount;
                throw;
            }
            return Result(outgoing, ingress, ingressError);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SavePreparedAsync(string id, Erc20SwapValues values, ArkadeSwapIntentStatus executionStatus,
        EvmPreparedTransaction prepared, CancellationToken cancellationToken)
    {
        var txid = prepared.TransactionHash;
        if (txid.Length != 66 || !txid.StartsWith("0x", StringComparison.Ordinal) || !txid[2..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("prepared EVM claim transaction identity is invalid");
        if (prepared.SignedTransaction is null || !prepared.SignedTransaction.StartsWith("0x02", StringComparison.Ordinal)
            || prepared.SignedTransaction.Length % 2 != 0 || !prepared.SignedTransaction[2..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("prepared EVM claim transaction is invalid");
        var intent = await LoadAsync(id, cancellationToken);
        var previous = intent.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid);
        var previousRaw = intent.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction);
        if (intent.Status != executionStatus || Values(intent) != values
            || previous is not null && previous != txid || previousRaw is not null && previousRaw != prepared.SignedTransaction)
            throw new InvalidOperationException("outgoing route already has a different prepared claim or state");
        intent.Metadata[ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid] = txid;
        intent.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction] = prepared.SignedTransaction;
        try
        {
            await _storage.SaveArkadeSwapIntent(intent, cancellationToken);
        }
        catch
        {
            if (previous is null) intent.Metadata.Remove(ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid);
            if (previousRaw is null) intent.Metadata.Remove(ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction);
            throw;
        }
    }

    private async Task<bool> HasExactOutgoingFundingAsync(ArkadeSwapIntent outgoing,
        CancellationToken cancellationToken)
    {
        var candidates = await _vtxos.GetVtxos(scripts: [outgoing.SwapPkScript], includeSpent: true,
            cancellationToken: cancellationToken);
        var live = candidates.Where(vtxo => !vtxo.IsSpent() && !vtxo.Swept).ToArray();
        if (live.Length == 0) return false;
        var chainTime = await _blockchain.GetChainTime(cancellationToken);
        if (live.Any(vtxo => !string.Equals(vtxo.Script, outgoing.SwapPkScript, StringComparison.OrdinalIgnoreCase)
                             || vtxo.Assets is { Count: > 0 } || vtxo.Unrolled
                             || vtxo.IsUnconfirmedOnchain() || !vtxo.CanSpendOffchain(chainTime)))
            throw new InvalidOperationException("outgoing lock funding is not currently spendable Arkade BTC");
        var total = live.Aggregate(0UL, (sum, vtxo) => checked(sum + vtxo.Amount));
        if (total != checked((ulong)outgoing.OfferAmount.Satoshi))
            throw new InvalidOperationException("outgoing lock funding must equal the exact Arkade-to-EVM quote amount");
        return true;
    }

    private async Task<ArkadeSwapIntent> ReconcileAsync(
        ArkadeSwapIntent intent, CancellationToken cancellationToken)
    {
        var uncertainOutgoing = intent.Type == ArkadeSwapIntentType.BtcToEvm
                                && intent.Status == ArkadeSwapIntentStatus.Resolved;
        if (!uncertainOutgoing && ArkadeSwapStateMachine.Terminal.Contains(intent.Status)
            || intent.Metadata.ContainsKey(ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid))
            return intent;

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var candidates = await _vtxos.GetVtxos(
            scripts: [intent.SwapPkScript], includeSpent: true, cancellationToken: cancellationToken);
        var lockup = candidates.FirstOrDefault(v => !v.IsSpent() && !v.Swept) ?? candidates.FirstOrDefault();
        ArkadeSwapIntentStatus? next;
        if (lockup is null)
        {
            next = ArkadeSwapStateMachine.NextOnClock(
                intent.Type, intent.Status, now, intent.RefundLocktime);
        }
        else
        {
            if (intent.Status == ArkadeSwapIntentStatus.Refundable
                && !lockup.IsSpent() && !lockup.Swept)
                return intent;
            var spender = lockup.SpentByTransactionId ?? lockup.SettledByTransactionId;
            var revealed = lockup.IsSpent()
                && intent.PaymentHash is { Length: > 0 } hash
                && spender is { Length: > 0 }
                && await SwapPreimageReader.FindAsync(
                    _transport, lockup.OutPoint, spender, hash, cancellationToken) is not null;
            next = uncertainOutgoing && revealed
                ? ArkadeSwapIntentStatus.Claimable
                : ArkadeSwapStateMachine.Next(intent.Type, intent.Status,
                    SwapObservation.From(lockup, now, intent.RefundLocktime, revealed));
        }
        if (next is null || next == intent.Status) return intent;

        var previousStatus = intent.Status;
        var previousSpentTxid = intent.SpentTxid;
        intent.Status = next.Value;
        if (lockup?.IsSpent() == true)
            intent.SpentTxid ??= lockup.ArkTxid ?? lockup.SpentByTransactionId;
        try
        {
            await _storage.SaveArkadeSwapIntent(intent, cancellationToken);
        }
        catch
        {
            intent.Status = previousStatus;
            intent.SpentTxid = previousSpentTxid;
            throw;
        }
        return intent;
    }

    private Erc20SwapValues Values(ArkadeSwapIntent outgoing)
    {
        var metadata = outgoing.EvmMetadata();
        if (outgoing.ToAssetId != $"eip155:{_policy.ChainId.ToString(CultureInfo.InvariantCulture)}/erc20:{_policy.TokenAddress}"
            || metadata.TokenAddress != _policy.TokenAddress || metadata.SwapContractAddress != _policy.SwapContractAddress
            || !BigInteger.TryParse(metadata.Amount, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || !BigInteger.TryParse(metadata.TimeoutBlock, NumberStyles.None, CultureInfo.InvariantCulture, out var timeout)
            || amount <= 0 || timeout <= 0)
            throw new InvalidOperationException("stored EVM quote differs from the configured execution policy");
        return new Erc20SwapValues(outgoing.PaymentHash!, amount, metadata.TokenAddress,
            metadata.ClaimAddress, metadata.RefundAddress, timeout);
    }

    private static void ValidateIdentity(ArkadeSwapIntent outgoing, ArkadeSwapIntent? ingress)
    {
        if (ingress is null) return;
        if (ingress.Type is not (ArkadeSwapIntentType.LightningToBtc or ArkadeSwapIntentType.OnchainToBtc)
            || ingress.WalletId != outgoing.WalletId || ingress.PaymentHash != outgoing.PaymentHash
            || ingress.WantAmount != outgoing.OfferAmount || ingress.OfferAmount < ingress.WantAmount
            || ingress.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.ComposedOutgoingSwapId) != outgoing.Id
            || ingress.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.ComposedPayoutPkScript) != outgoing.SwapPkScript)
            throw new InvalidOperationException("persisted intents do not form the requested composed route");
    }

    private async Task<ArkadeSwapIntent> LoadAsync(string id, CancellationToken cancellationToken) =>
        await _storage.GetArkadeSwapIntent(id, cancellationToken)
        ?? throw new InvalidOperationException("composed route intent is not available");

    private static ComposedSwapExecutionResult Result(
        ArkadeSwapIntent outgoing, ArkadeSwapIntent? ingress, string? ingressError = null) =>
        new(outgoing.Id, outgoing.Status, ingress?.Id, ingress?.Status,
            outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimTxid), ReadProof(outgoing),
            outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmDeliveredAmount))
        {
            IngressError = ingressError
        };

    private static EvmLockProof? ReadProof(ArkadeSwapIntent intent)
    {
        var observed = intent.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmLockObservedAtBlock);
        var proven = intent.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmLockProvenAtBlock);
        var timestamp = intent.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmLockProvenBlockTimestamp);
        if (observed is null && proven is null && timestamp is null) return null;
        if (!BigInteger.TryParse(observed, NumberStyles.None, CultureInfo.InvariantCulture, out var observedAt)
            || !BigInteger.TryParse(proven, NumberStyles.None, CultureInfo.InvariantCulture, out var provenAt)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var blockTimestamp)
            || observedAt < provenAt || provenAt < 0 || blockTimestamp < 0)
            throw new InvalidOperationException("stored EVM lock proof is invalid");
        return new EvmLockProof(observedAt, provenAt, blockTimestamp);
    }
}
