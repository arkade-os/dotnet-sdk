using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Fees;
using NArk.Core.Helpers;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Evm.Dex;
using NArk.Swaps.Evm.Models;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;

namespace NArk.Swaps.Evm;

/// <summary>
/// Refund paths: chain-quote renegotiation, the EVM-side <c>ERC20Swap</c> refund, and the
/// Ark-side <c>ChainArkToEvm</c> refund (cooperative first, then the refund-without-receiver
/// batch intent). Mirrors <c>BoltzSwapProvider.Refunds.cs</c>&apos;s split.
/// </summary>
public partial class EvmChainSwapProvider
{

    /// <summary>
    /// Asks Boltz for a new chain-swap quote based on the amount actually funded at the
    /// lockup, and accepts it. Returns <c>true</c> on success (quote returned and accepted,
    /// local <see cref="ArkSwap.ExpectedAmount"/> updated). Returns <c>false</c> if Boltz
    /// refuses the quote — typically because the funded amount falls outside Boltz's published
    /// limits for this pair — in which case the caller should fall through to the refund path.
    /// </summary>
    /// <remarks>
    /// Wired into <see cref="PollSwapAsync"/> on the <c>transaction.lockupFailed</c> Boltz
    /// status. Mirrors <c>NArk.Swaps.Boltz.BoltzSwapProvider.TryRenegotiateChainSwap</c>
    /// exactly — same currency-agnostic <c>GET</c>/<c>POST v2/swap/chain/{id}/quote</c>
    /// endpoints via the shared <see cref="BoltzClient"/>, just bounded against this pair's own
    /// limits (<see cref="GetLimitsAsync"/>) instead of <c>BoltzLimitsValidator</c>, which
    /// hardcodes the <c>BTC</c>/<c>ARK</c> pair keys and can't see our <c>TBTC</c>/etc. pair.
    /// </remarks>
    private async Task<bool> TryRenegotiateChainSwap(ArkSwap swap, CancellationToken ct)
    {
        try
        {
            var newQuote = await _boltzClient.GetChainQuoteAsync(swap.SwapId, ct);
            if (newQuote is null)
            {
                _logger?.LogWarning("Swap {SwapId}: Boltz returned a null chain quote", swap.SwapId);
                return false;
            }

            // Bound the renegotiated amount before accepting it and persisting it as the
            // swap's new ExpectedAmount, same rationale as the Boltz-native path: a 0/negative
            // quote is a parse/protocol bug, and an out-of-limits amount would be rejected by
            // AcceptChainQuoteAsync anyway, but checking locally avoids a wire round-trip.
            if (swap.Route is null)
            {
                _logger?.LogWarning("Swap {SwapId}: no Route recorded, cannot validate renegotiated quote", swap.SwapId);
                return false;
            }
            var limits = await GetLimitsAsync(swap.Route, ct);
            if (newQuote.Amount <= 0 || newQuote.Amount < limits.MinAmount || newQuote.Amount > limits.MaxAmount)
            {
                _logger?.LogWarning(
                    "Swap {SwapId}: rejecting renegotiated chain quote with out-of-bounds amount {Amount} sats " +
                    "(limits: min={Min}, max={Max})",
                    swap.SwapId, newQuote.Amount, limits.MinAmount, limits.MaxAmount);
                return false;
            }

            await _boltzClient.AcceptChainQuoteAsync(swap.SwapId, newQuote, ct);
            _logger?.LogInformation(
                "Swap {SwapId}: chain quote renegotiated — original {Original} sats -> new {New} sats",
                swap.SwapId, swap.ExpectedAmount, newQuote.Amount);

            var updated = swap with
            {
                ExpectedAmount = newQuote.Amount,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _swapStorage.SaveSwap(swap.WalletId, updated, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Boltz returns 4xx both for out-of-limits amounts and for an already-accepted
            // quote (e.g. an overlapping poll tick won the race). Disambiguate by re-reading
            // server-side status: if Boltz has moved the swap past lockupFailed, renegotiation
            // effectively succeeded.
            try
            {
                var currentStatus = await _boltzClient.GetSwapStatusAsync(swap.SwapId, ct);
                if (currentStatus is not null &&
                    !string.IsNullOrEmpty(currentStatus.Status) &&
                    !string.Equals(currentStatus.Status, BoltzSwapStatus.TransactionLockupFailed, StringComparison.Ordinal))
                {
                    _logger?.LogInformation(
                        "Swap {SwapId}: AcceptChainQuoteAsync 4xx'd but Boltz status is {Status} — " +
                        "treating as renegotiated by a concurrent poll",
                        swap.SwapId, currentStatus.Status);
                    return true;
                }
            }
            catch (Exception probeEx) when (probeEx is not OperationCanceledException)
            {
                _logger?.LogDebug(probeEx,
                    "Swap {SwapId}: status probe after renegotiation failure also failed; falling back to refund",
                    swap.SwapId);
            }

            _logger?.LogWarning(ex, "Swap {SwapId}: chain quote renegotiation refused by Boltz", swap.SwapId);
            return false;
        }
    }
    private async Task TryRefundEvmLockupAsync(ArkSwap swap, CancellationToken ct)
    {
        var preimageHex = swap.Get(SwapMetadata.Preimage)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: missing preimage in metadata.");
        var preimageHash = Hashes.SHA256(Convert.FromHexString(preimageHex));

        var client = await GetEvmChainClientAsync(ct);
        var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
        var tokenAddress = info.Tokens[_options.PairCurrency];

        // Idempotency, same shape as the lock/claim paths — a refund already on-chain (or still
        // in flight) must not be re-broadcast; ERC20Swap deletes the swap entry on refund, so a
        // second attempt reverts. The extra WaitForLockup/NothingLocked distinction is what keeps
        // an un-indexed lockup from being mistaken for an empty swap.
        var refundedOnChain = await client.FindRefundEventAsync(preimageHash, ct) is not null;
        var pendingRefundTx = swap.Get(SwapMetadata.EvmRefundTxId);
        var lockup = await client.FindLockupEventAsync(preimageHash, ct);
        var lockTxId = swap.Get(SwapMetadata.EvmLockTxId);

        var action = EvmIdempotencyResolver.ResolveRefund(
            refundedOnChain, pendingRefundTx, lockup is not null, lockTxId);

        switch (action)
        {
            case EvmRefundAction.AlreadyRefunded:
                _logger?.LogInformation(
                    "Swap {SwapId}: ERC20Swap refund for this preimage hash is already on-chain", swap.SwapId);
                await MarkSwapTerminalAsync(swap, ArkSwapStatus.Refunded, null, ct);
                return;

            case EvmRefundAction.AwaitBroadcast:
                _logger?.LogInformation(
                    "Swap {SwapId}: refund tx {TxHash} already broadcast — waiting for its receipt",
                    swap.SwapId, pendingRefundTx);
                await client.WaitForReceiptAsync(pendingRefundTx!, ct);
                await MarkSwapTerminalAsync(swap, ArkSwapStatus.Refunded, null, ct);
                return;

            case EvmRefundAction.WaitForLockup:
                _logger?.LogWarning(
                    "Swap {SwapId}: expired and no Lockup event visible, but lock tx {TxHash} was broadcast — " +
                    "keeping the swap active so a later poll can refund it once the lockup is indexed",
                    swap.SwapId, lockTxId);
                return;

            case EvmRefundAction.NothingLocked:
                // Swap expired before we ever locked (LockEvmAsync never ran, or the caller never
                // funded it) — nothing to refund. Unlike TryClaimEvmLockupAsync's null case, this
                // is permanent, not transient: mark Failed so the poll loop stops retrying forever.
                if (swap.Status != ArkSwapStatus.Failed)
                {
                    _logger?.LogInformation(
                        "Swap {SwapId}: expired with no EVM lockup observed and no lock tx recorded — marking Failed",
                        swap.SwapId);
                    await MarkSwapTerminalAsync(
                        swap, ArkSwapStatus.Failed, "Swap expired before any funds were locked", ct);
                }
                return;
        }

        var refundTxHash = await client.SendRefundAsync(
            preimageHash, lockup!.Amount, tokenAddress, lockup.ClaimAddress, lockup.Timelock, ct);
        await RecordEvmTxIdAsync(swap.SwapId, SwapMetadata.EvmRefundTxId, refundTxHash, ct);

        await client.WaitForReceiptAsync(refundTxHash, ct);

        // Same rationale as TryClaimEvmLockupAsync — but more important here: this is OUR OWN
        // refund of OUR OWN funds, and empirically (verified live this session) Boltz's own
        // status can stay stuck on swap.expired indefinitely since it has no direct incentive
        // to track a refund that doesn't move its funds. Waiting on Boltz's indexer here would
        // leave the swap Pending forever despite the refund having already succeeded on-chain.
        await MarkSwapTerminalAsync(swap, ArkSwapStatus.Refunded, null, ct);
        _logger?.LogInformation("Swap {SwapId}: refunded EVM lockup", swap.SwapId);
    }

    // ─── Ark-side refund (ChainArkToEvm) ────────────────────────────────────
    // Mirrors NArk.Swaps.Boltz.BoltzSwapProvider.Refunds.cs's ChainArkToBtc path exactly:
    // cooperative refund first (Boltz co-signs via the same generic
    // POST /v2/swap/chain/{id}/refund/ark endpoint that path uses — it's keyed only by
    // swapId, not scoped to any particular "to" currency), falling back to the
    // refund-without-receiver batch-intent path once RefundLocktime elapses (arkd's
    // checkpoint endpoint rejects that script's absolute-CLTV directly via a normal
    // SpendingService.Spend, so it has to go through IIntentGenerationService instead).

    private async Task TryCoopRefundArkToEvm(ArkSwap swap, CancellationToken ct)
    {
        _logger?.LogInformation(
            "Swap {SwapId}: chain swap expired (ChainArkToEvm), attempting cooperative refund", swap.SwapId);

        // A refund-without-receiver batch may already be in flight (or settled) from a
        // previous poll. Resolve that first: once the batch settles the lockup VTXO is
        // spent, and without this check the coop attempt below would see "no lockup" and
        // incorrectly mark the swap Failed.
        var refundIntentStatus = await CheckRefundWithoutReceiverIntentAsync(swap, ct);
        if (refundIntentStatus is not null) return;

        if (await CoopRefundArkToEvmChainSwap(swap, ct)) return;

        // Nothing to recover — mark Failed so the poll stops retrying.
        var vtxosLocked = await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript], cancellationToken: ct);
        if (vtxosLocked.Count == 0 && swap.Status != ArkSwapStatus.Failed)
        {
            _logger?.LogInformation("Swap {SwapId}: expired with no observable lockup — marking Failed", swap.SwapId);
            await MarkSwapTerminalAsync(swap, ArkSwapStatus.Failed, "Swap expired before any funds were locked", ct);
        }
    }

    private async Task<bool> CoopRefundArkToEvmChainSwap(ArkSwap swap, CancellationToken ct)
    {
        if (swap.SwapType != ArkSwapType.ChainArkToEvm) return false;
        if (swap.Status == ArkSwapStatus.Refunded) return true;

        ArkServerInfo? serverInfo = null;
        VHTLCContract? contract = null;
        ArkVtxo? vtxo = null;
        IDestination? refundDestination = null;
        try
        {
            serverInfo = await _clientTransport.GetServerInfoAsync(ct);

            var matchedSwapContracts = await _contractStorage.GetContracts(
                walletIds: [swap.WalletId], scripts: [swap.ContractScript], cancellationToken: ct);
            var matchedSwapContractEntity = matchedSwapContracts.SingleOrDefault(e => e.Type == VHTLCContract.ContractType);
            if (matchedSwapContractEntity is null)
            {
                _logger?.LogWarning("Swap {SwapId}: VHTLC contract row not found for Ark refund", swap.SwapId);
                return false;
            }
            contract = ArkContractParser.Parse(matchedSwapContractEntity.Type, matchedSwapContractEntity.AdditionalData,
                serverInfo.Network) as VHTLCContract;
            if (contract is null)
            {
                _logger?.LogWarning("Swap {SwapId}: failed to parse VHTLC contract for Ark refund", swap.SwapId);
                return false;
            }

            // Same arkd refresh pattern BoltzSwapProvider.Refunds.cs uses — closes the gap
            // between the indexer subscription stream and what arkd actually has right now.
            await foreach (var freshVtxo in _clientTransport.GetVtxoByScriptsAsSnapshot(
                               new HashSet<string> { swap.ContractScript }, ct))
            {
                await _vtxoStorage.UpsertVtxo(freshVtxo, ct);
            }

            var vtxos = await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript], cancellationToken: ct);
            if (vtxos.Count == 0)
            {
                _logger?.LogWarning("Swap {SwapId}: no VTXOs at VHTLC script for Ark refund", swap.SwapId);
                return false;
            }

            vtxo = vtxos.FirstOrDefault(v => (long)v.Amount == swap.ExpectedAmount && !v.IsSpent());
            if (vtxo is null)
            {
                _logger?.LogWarning(
                    "Swap {SwapId}: no unspent VTXO of expected amount {ExpectedAmount} at swap script (have {Total})",
                    swap.SwapId, swap.ExpectedAmount, vtxos.Count);
                return false;
            }

            var timeHeight = await _chainTimeProvider.GetChainTime(ct);
            if (!vtxo.CanSpendOffchain(timeHeight))
            {
                _logger?.LogDebug("Swap {SwapId}: VHTLC VTXO not spendable offchain (spent/swept/expired)", swap.SwapId);
                return false;
            }

            (refundDestination, swap) = await swap.GetOrDeriveRefundDestinationAsync(
                _contractService, _swapStorage, serverInfo.Network, ct);

            var arkCoin = contract.ToCoopRefundCoin(swap.WalletId, vtxo);

            var (arkTx, checkpoints) = await _transactionBuilder.ConstructArkTransaction(
                [arkCoin], [new ArkTxOut(ArkTxOutType.Vtxo, arkCoin.Amount, refundDestination)], serverInfo, ct);

            if (checkpoints.Count != 1)
                throw new InvalidOperationException(
                    $"Swap {swap.SwapId}: expected exactly 1 checkpoint for a single-input Ark refund, " +
                    $"got {checkpoints.Count}. Protocol invariant violated or SDK out of sync.");
            var checkpoint = checkpoints.First();

            var refundResponse = await _boltzClient.RefundChainSwapArkAsync(swap.SwapId,
                new ChainArkRefundRequest { Transaction = arkTx.ToBase64(), Checkpoint = checkpoint.Psbt.ToBase64() }, ct);

            var boltzSignedRefundPsbt = PSBT.Parse(refundResponse.Transaction, serverInfo.Network);
            var boltzSignedCheckpointPsbt = PSBT.Parse(refundResponse.Checkpoint, serverInfo.Network);
            arkTx.UpdateFrom(boltzSignedRefundPsbt);
            checkpoint.Psbt.UpdateFrom(boltzSignedCheckpointPsbt);

            await _transactionBuilder.SubmitArkTransaction([arkCoin], arkTx, [checkpoint], ct);

            await MarkSwapTerminalAsync(swap, ArkSwapStatus.Refunded, null, ct);
            _logger?.LogInformation("Swap {SwapId}: ARK->EVM cooperative refund completed", swap.SwapId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Swap {SwapId}: ARK->EVM cooperative refund failed", swap.SwapId);
            if (contract is not null && vtxo is not null && refundDestination is not null && serverInfo is not null)
                return await TryRefundWithoutReceiverAsync(swap, contract, vtxo, refundDestination, serverInfo, ct);
            return false;
        }
    }

    /// <summary>
    /// Fallback for when Boltz permanently refuses the cooperative co-sign: submits the VHTLC
    /// spend via the <c>refundWithoutReceiver</c> tapscript (server + sender, absolute CLTV) as
    /// an Arkade batch intent once <see cref="VHTLCContract.RefundLocktime"/> has elapsed.
    /// The batch path is required because arkd's checkpoint (<c>SubmitTx</c>) endpoint rejects
    /// this closure's block-height CLTV directly (<c>blockTypeAllowed=false</c>); the
    /// batch/<c>JoinRound</c> path sets <c>blockTypeAllowed=true</c> and enforces the locktime
    /// via the forfeit tx's <c>nLockTime</c> instead.
    /// </summary>
    private async Task<bool> TryRefundWithoutReceiverAsync(
        ArkSwap swap, VHTLCContract contract, ArkVtxo vtxo, IDestination refundDestination,
        ArkServerInfo serverInfo, CancellationToken ct)
    {
        var timeHeight = await _chainTimeProvider.GetChainTime(ct);

        var elapsed = contract.RefundLocktime.IsTimeLock
            ? contract.RefundLocktime.Date <= timeHeight.Timestamp
            : (uint)timeHeight.Height >= contract.RefundLocktime.Value;

        if (!elapsed)
        {
            _logger?.LogDebug("Swap {SwapId}: RefundLocktime {Locktime} not yet elapsed — retrying coop on next poll",
                swap.SwapId, contract.RefundLocktime.Value);
            return false;
        }

        // If we already submitted a refund intent, check its state before creating another.
        var intentStatus = await CheckRefundWithoutReceiverIntentAsync(swap, ct);
        if (intentStatus is not null) return intentStatus.Value;

        if (_intentGenerationService is null)
        {
            _logger?.LogError(
                "Swap {SwapId}: cannot generate refund intent — no IIntentGenerationService registered", swap.SwapId);
            return false;
        }

        try
        {
            _logger?.LogInformation(
                "Swap {SwapId}: RefundLocktime elapsed, submitting refund-without-receiver batch intent", swap.SwapId);

            var arkCoin = new ArkCoin(swap.WalletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
                vtxo.OutPoint, vtxo.TxOut, contract.Sender,
                contract.CreateRefundWithoutReceiverScript(), null, contract.RefundLocktime, null,
                vtxo.Swept, vtxo.Unrolled);

            // Estimate fee against the full input amount, then deduct to get the net output.
            var feeEstimator = new DefaultFeeEstimator(_clientTransport, _chainTimeProvider);
            var fee = await feeEstimator.EstimateFeeAsync(
                [arkCoin], [new ArkTxOut(ArkTxOutType.Vtxo, arkCoin.Amount, refundDestination)], ct);
            var netOutput = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(arkCoin.Amount.Satoshi - fee), refundDestination);

            var spec = new ArkIntentSpec([arkCoin], [netOutput], DateTimeOffset.UtcNow, null);
            var intentTxId = await _intentGenerationService.GenerateManualIntent(swap.WalletId, spec, ct);
            _intentToSwapId[intentTxId] = swap.SwapId;

            var updatedSwap = swap with
            {
                Metadata = new Dictionary<string, string>(swap.Metadata ?? []) { [SwapMetadata.RefundIntentTxId] = intentTxId },
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _swapStorage.SaveSwap(swap.WalletId, updatedSwap, ct);
            _logger?.LogInformation(
                "Swap {SwapId}: refund intent {IntentTxId} submitted — waiting for Arkade batch settlement",
                swap.SwapId, intentTxId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Swap {SwapId}: refund-without-receiver failed", swap.SwapId);
            return false;
        }
    }

    /// <summary>
    /// Inspects the in-flight refund-without-receiver batch intent (if any) recorded in
    /// <see cref="SwapMetadata.RefundIntentTxId"/> and reports what the caller should do:
    /// <c>true</c> — the batch settled, the swap is now <see cref="ArkSwapStatus.Refunded"/>;
    /// <c>false</c> — an intent is still in flight, the caller should wait and not re-attempt
    /// the cooperative refund or mark the swap failed; <c>null</c> — no intent recorded, or the
    /// last one reached a terminal failure, caller should (re-)submit / fall through.
    /// </summary>
    private async Task<bool?> CheckRefundWithoutReceiverIntentAsync(ArkSwap swap, CancellationToken ct)
    {
        var existingIntentTxId = swap.Get(SwapMetadata.RefundIntentTxId);
        if (existingIntentTxId is null) return null;

        var intents = await _intentStorage.GetIntents(intentTxIds: [existingIntentTxId], cancellationToken: ct);
        var intent = intents.FirstOrDefault();
        if (intent is null) return null;

        // Re-arm the event trigger in case we restarted after saving the metadata.
        _intentToSwapId.TryAdd(existingIntentTxId, swap.SwapId);

        if (intent.State == ArkIntentState.BatchSucceeded)
        {
            await MarkSwapTerminalAsync(swap, ArkSwapStatus.Refunded, null, ct);
            _intentToSwapId.TryRemove(existingIntentTxId, out _);
            _logger?.LogInformation("Swap {SwapId}: refund-without-receiver batch succeeded", swap.SwapId);
            return true;
        }

        if (intent.State is ArkIntentState.WaitingToSubmit or ArkIntentState.WaitingForBatch or ArkIntentState.BatchInProgress)
        {
            _logger?.LogDebug("Swap {SwapId}: refund intent {IntentTxId} still in state {State} — waiting for batch",
                swap.SwapId, existingIntentTxId, intent.State);
            return false;
        }

        // Terminal failure (BatchFailed / Cancelled) — remove and signal re-submit.
        _logger?.LogWarning(
            "Swap {SwapId}: refund intent {IntentTxId} reached terminal failure state {State} — re-submitting",
            swap.SwapId, existingIntentTxId, intent.State);
        _intentToSwapId.TryRemove(existingIntentTxId, out _);
        return null;
    }

    /// <summary>Triggered when an in-flight refund intent's batch session completes (succeeds,
    /// fails, or is cancelled) — fires an immediate poll via the existing websocket trigger
    /// channel rather than waiting for the next routine poll tick.</summary>
    private void OnRefundIntentChanged(object? sender, ArkIntent intent)
    {
        if (!_intentToSwapId.TryGetValue(intent.IntentTxId, out var swapId))
            return;

        if (intent.State is ArkIntentState.BatchSucceeded or ArkIntentState.BatchFailed or ArkIntentState.Cancelled)
        {
            _logger?.LogInformation(
                "Refund intent {IntentTxId} for swap {SwapId} reached terminal state {State} — triggering poll",
                intent.IntentTxId, swapId, intent.State);
            _wsTriggerChannel.Writer.TryWrite(swapId);
        }
    }
}
