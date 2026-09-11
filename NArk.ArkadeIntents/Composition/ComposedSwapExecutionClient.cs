using System.Globalization;
using System.Numerics;
using NArk.Abstractions.Contracts;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
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
    EvmLockProof? LockProof = null, string? DeliveredAmount = null);

/// <summary>Advances persisted composed routes without disclosing their SDK-held secrets in results.</summary>
/// <remarks>The host must serialize a route across processes; intent storage does not expose compare-and-swap.</remarks>
public sealed class ComposedSwapExecutionClient
{
    private readonly IArkadeIntentStorage _storage;
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
    /// <param name="contracts">Imported covenant descriptors.</param>
    /// <param name="transport">Arkade network facts.</param>
    /// <param name="lightning">NI Lightning receive and outgoing refund implementation.</param>
    /// <param name="onchain">NI onchain receive implementation, optional for other rails.</param>
    /// <param name="rpc">Configured EVM chain reader.</param>
    /// <param name="sender">Host sender that saves a deterministic transaction identity before broadcast.</param>
    /// <param name="policy">Expected chain, token, contract and proof bounds.</param>
    /// <param name="timeProvider">Clock used to enforce safety windows.</param>
    public ComposedSwapExecutionClient(IArkadeIntentStorage storage, IContractStorage contracts,
        IClientTransport transport, LightningIntentsClient lightning, OnchainIntentsClient? onchain,
        IEvmSwapRpc rpc, IEvmDurableTransactionSender sender, EvmSendPolicy policy, TimeProvider? timeProvider = null)
    {
        _storage = storage;
        _contracts = contracts;
        _transport = transport;
        _lightning = lightning;
        _onchain = onchain;
        _policy = policy;
        _time = timeProvider ?? TimeProvider.System;
        _chain = new EvmSwapChainClient(rpc, sender, policy, _time);
    }

    /// <summary>Advances one route; pending observations do nothing and verified completion is repeatable.</summary>
    /// <param name="outgoingSwapId">Persisted BtcToEvm RFQ id.</param>
    /// <param name="ingressSwapId">Persisted Lightning/onchain receive RFQ id, or null for direct Arkade.</param>
    /// <param name="cancellationToken">Cancels reads or submission; prepared EVM identity survives an uncertain broadcast.</param>
    /// <returns>Public lifecycle and verified transaction identity only.</returns>
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
            if (outgoing.Status == ArkadeSwapIntentStatus.Fulfilled)
            {
                if (!outgoing.Metadata.ContainsKey(ArkadeSwapMetadataKeys.EvmClaimTxid))
                    throw new InvalidOperationException("completed EVM route has no verified claim transaction");
                return Result(outgoing, ingress);
            }
            if (outgoing.Status == ArkadeSwapIntentStatus.Refundable)
                return Result(await _lightning.RefundNonInteractiveAsync(outgoing.Id, cancellationToken), ingress);

            if (outgoing.Status == ArkadeSwapIntentStatus.Pending && ingress?.Status == ArkadeSwapIntentStatus.Claimable)
            {
                Values(outgoing);
                var server = await _transport.GetServerInfoAsync(cancellationToken);
                var contract = await LightningCorridor.LoadLockupAsync(_contracts, ingress.SwapPkScript,
                    ingress.Id, server.Network, cancellationToken);
                await ComposedRouteExecutionGuard.ValidateIngressAsync(
                    _storage, _contracts, ingress, contract, server.Network, _time.GetUtcNow().ToUnixTimeSeconds(), cancellationToken);
                ingress = ingress.Type == ArkadeSwapIntentType.LightningToBtc
                    ? await _lightning.ClaimNonInteractiveAsync(ingress.Id, cancellationToken)
                    : await (_onchain ?? throw new InvalidOperationException("onchain ingress client is unavailable"))
                        .ClaimNonInteractiveAsync(ingress.Id, cancellationToken);
                outgoing = await LoadAsync(outgoing.Id, cancellationToken);
            }

            if (outgoing.Status != ArkadeSwapIntentStatus.Claimable)
                return Result(outgoing, ingress);
            if (string.IsNullOrEmpty(outgoing.SpentTxid))
                throw new InvalidOperationException("outgoing Claimable requires a proven Arkade spend");
            var values = Values(outgoing);
            var preimage = ComposedRouteExecutionGuard.ValidateSecret(outgoing);
            var submitted = outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid);
            if (submitted is null)
            {
                var proof = await _chain.ProveLockAsync(values, cancellationToken);
                outgoing.Metadata[ArkadeSwapMetadataKeys.EvmLockObservedAtBlock] = proof.ObservedAtBlock.ToString(CultureInfo.InvariantCulture);
                outgoing.Metadata[ArkadeSwapMetadataKeys.EvmLockProvenAtBlock] = proof.ProvenAtBlock.ToString(CultureInfo.InvariantCulture);
                outgoing.Metadata[ArkadeSwapMetadataKeys.EvmLockProvenBlockTimestamp] = proof.ProvenBlockTimestamp.ToString(CultureInfo.InvariantCulture);
                await _storage.SaveArkadeSwapIntent(outgoing, cancellationToken);
            }
            var verified = submitted is not null
                ? await _chain.VerifyClaimAsync(submitted, values, preimage, cancellationToken)
                : await _chain.ClaimForAsync(values, preimage,
                    (txid, token) => SavePreparedAsync(outgoing.Id, values, txid, token), cancellationToken);
            outgoing = await LoadAsync(outgoing.Id, cancellationToken);
            if (Values(outgoing) != values || outgoing.Status != ArkadeSwapIntentStatus.Claimable)
                throw new InvalidOperationException("outgoing route changed during EVM claim verification");
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
                outgoing.Status = ArkadeSwapIntentStatus.Claimable;
                if (previous is null) outgoing.Metadata.Remove(ArkadeSwapMetadataKeys.EvmClaimTxid);
                else outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimTxid] = previous;
                if (previousAmount is null) outgoing.Metadata.Remove(ArkadeSwapMetadataKeys.EvmDeliveredAmount);
                else outgoing.Metadata[ArkadeSwapMetadataKeys.EvmDeliveredAmount] = previousAmount;
                throw;
            }
            return Result(outgoing, ingress);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SavePreparedAsync(string id, Erc20SwapValues values, string txid, CancellationToken cancellationToken)
    {
        if (txid.Length != 66 || !txid.StartsWith("0x", StringComparison.Ordinal) || !txid[2..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("prepared EVM claim transaction identity is invalid");
        var intent = await LoadAsync(id, cancellationToken);
        var previous = intent.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid);
        if (intent.Status != ArkadeSwapIntentStatus.Claimable || Values(intent) != values
            || previous is not null && previous != txid)
            throw new InvalidOperationException("outgoing route already has a different prepared claim or state");
        intent.Metadata[ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid] = txid;
        try
        {
            await _storage.SaveArkadeSwapIntent(intent, cancellationToken);
        }
        catch
        {
            if (previous is null) intent.Metadata.Remove(ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid);
            throw;
        }
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

    private static ComposedSwapExecutionResult Result(ArkadeSwapIntent outgoing, ArkadeSwapIntent? ingress) =>
        new(outgoing.Id, outgoing.Status, ingress?.Id, ingress?.Status,
            outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimTxid), ReadProof(outgoing),
            outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmDeliveredAmount));

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
