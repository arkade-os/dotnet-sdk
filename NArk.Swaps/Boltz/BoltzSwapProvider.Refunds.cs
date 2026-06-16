using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.Swaps.Common;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Swaps.Boltz;

public partial class BoltzSwapProvider
{
    // REFUNDS 
    // Submarine swaps:
    //For the swap states invoice.failedToPay, swap.expired where bitcoin were sent,
    //and transaction.lockupFailed, the user needs to submit a refund transaction to reclaim the locked chain bitcoin.
    //For more information about how Boltz API clients can construct and submit refund transactions for users,
    //check the Claim & Refund Transactions section.
    // 
    // The state transaction.lockupFailed is not final and changes to swap.expired after the swap expired;
    // the failure reason will be kept and informs e.g. if the user sending too little or too much was the reason for the swap to fail. T
    // he states invoice.failedToPay and swap.expired are final. Boltz is not monitoring user's refund transactions.
   internal async Task RequestSubmarineCoopRefund(ArkSwap swap, SwapStatusResponse swapStatus, CancellationToken cancellationToken = default)
   {
        if (swap.SwapType != ArkSwapType.Submarine)
        {
            throw new InvalidOperationException("Only submarine swaps can be refunded");
        }

        if (swap.Status == ArkSwapStatus.Refunded)
        {
            return;
        }
        
        _logger?.LogInformation("Swap {SwapId}: Boltz status '{BoltzStatus}' is refundable, initiating cooperative refund",
            swap.SwapId, swapStatus.Status);

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        var matchedSwapContracts =
            await _contractStorage.GetContracts(walletIds: [swap.WalletId], scripts: [swap.ContractScript],
                cancellationToken: cancellationToken);

        var matchedSwapContractForSwapWallet =
            matchedSwapContracts.Single(entity => entity.Type == VHTLCContract.ContractType);

        // Parse the VHTLC contract
        if (ArkContractParser.Parse(matchedSwapContractForSwapWallet.Type,
                matchedSwapContractForSwapWallet.AdditionalData, serverInfo.Network) is not VHTLCContract contract)
        {
            throw new InvalidOperationException("Failed to parse VHTLC contract for refund");
        }

        // Poll arkd directly for VTXOs at the swap script.
        await foreach (var freshVtxo in _clientTransport.GetVtxoByScriptsAsSnapshot(
                           new HashSet<string> { swap.ContractScript }, cancellationToken))
        {
            await _vtxoStorage.UpsertVtxo(freshVtxo, cancellationToken);
        }

        // Get VTXOs for this contract
        var vtxos = await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript],
            cancellationToken: cancellationToken);
        if (vtxos.Count == 0)
        {
            _logger?.LogWarning("Swap {SwapId}: no VTXOs found for cooperative refund — scheduling near-term retry", swap.SwapId);
            ScheduleNearTermRetry(swap.SwapId, TimeSpan.FromSeconds(2));
            return;
        }

        // Boltz only cooperatively signs a refund for the canonical lockup
        // VTXO it tracks for this swap (matches swap.ExpectedAmount). If the
        // user accidentally double-funded the swap script (e.g., paid twice
        // after a perceived stall), additional VTXOs sitting at the same
        // script can only be recovered via the timelock path — which is
        // exactly what SweeperService + SwapSweepPolicy handle once the
        // refund CSV elapses. So here we narrow to the canonical VTXO and
        // leave any extras for the sweeper.
        var vtxo = vtxos.FirstOrDefault(v => (long)v.Amount == swap.ExpectedAmount && !v.IsSpent());
        if (vtxo is null)
        {
            _logger?.LogWarning(
                "Swap {SwapId}: no unspent VTXO of expected amount {ExpectedAmount} found among {Total} VTXO(s) at swap script — scheduling near-term retry; if canonical lockup never arrives, SweeperService handles extras via timelock",
                swap.SwapId, swap.ExpectedAmount, vtxos.Count);
            ScheduleNearTermRetry(swap.SwapId, TimeSpan.FromSeconds(2));
            return;
        }
        if (vtxos.Count > 1)
        {
            _logger?.LogInformation(
                "Swap {SwapId}: swap script has {Total} VTXO(s); cooperatively refunding the canonical {ExpectedAmount}-sat lockup, leaving {Extras} extra(s) for SweeperService",
                swap.SwapId, vtxos.Count, swap.ExpectedAmount, vtxos.Count - 1);
        }

        var timeHeight = await _chainTimeProvider.GetChainTime(cancellationToken);
        if (!vtxo.CanSpendOffchain(timeHeight))
            return;

        IDestination refundDestination;
        (refundDestination, swap) = await swap.GetOrDeriveRefundDestinationAsync(
            _contractService, _swapsStorage, serverInfo.Network, cancellationToken);

        try
        {
            var arkCoin = contract.ToCoopRefundCoin(swap.WalletId, vtxo);

            var (arkTx, checkpoints) =
                await _transactionBuilder.ConstructArkTransaction([arkCoin],
                    [new ArkTxOut(ArkTxOutType.Vtxo, arkCoin.Amount, refundDestination)],
                    serverInfo, cancellationToken);

            var checkpoint = checkpoints.Single();

            // Request Boltz to co-sign the refund
            var refundRequest = new SubmarineRefundRequest
            {
                Transaction = arkTx.ToBase64(),
                Checkpoint = checkpoint.Psbt.ToBase64()
            };

            var refundResponse =
                await _boltzClient.RefundSubmarineSwapAsync(swap.SwapId, refundRequest, cancellationToken);

            // Parse Boltz-signed transactions
            var boltzSignedRefundPsbt = PSBT.Parse(refundResponse.Transaction, serverInfo.Network);
            var boltzSignedCheckpointPsbt = PSBT.Parse(refundResponse.Checkpoint, serverInfo.Network);

            // Combine signatures
            arkTx.UpdateFrom(boltzSignedRefundPsbt);
            checkpoint.Psbt.UpdateFrom(boltzSignedCheckpointPsbt);

            await _transactionBuilder.SubmitArkTransaction([arkCoin], arkTx, [checkpoint],
                cancellationToken);

            var newSwap =
                swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.UtcNow };

            await _swapsStorage.SaveSwap(newSwap.WalletId, newSwap, cancellationToken);
            RaiseSwapStatusChanged(newSwap);
            _logger?.LogInformation("Swap {SwapId}: cooperative refund completed successfully", swap.SwapId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Swap {SwapId}: cooperative refund failed", swap.SwapId);
            // If UnilateralExitService is registered, start the on-chain exit instead of retrying coop.
            // TryStartVhtlcUnilateralExitAsync is idempotent — safe to call on every poll.
            if (!await TryStartVhtlcUnilateralExitAsync(swap, cancellationToken))
                throw; // no exit service or no VTXO yet — let poll loop retry
            return; // exit session created, swap marked Refunded
        }

        // Synchronization barrier
        try
        {
            await using var @lock =
                await _safetyService.LockKeyAsync($"contract::{contract.GetArkAddress().ScriptPubKey.ToHex()}",
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Refund already succeeded — cancellation during disposal is benign.
        }
    }
   
   
    /// <summary>
    /// Asks Boltz for a new chain-swap quote based on the amount actually
    /// funded at the lockup, and accepts it. Returns <c>true</c> on success
    /// (quote returned and accepted, local <see cref="ArkSwap.ExpectedAmount"/>
    /// updated). Returns <c>false</c> if Boltz refuses the quote — typically
    /// because the funded amount falls outside Boltz's published limits, in
    /// which case the caller should fall through to the refund path.
    /// </summary>
    /// <remarks>
    /// Wired into <see cref="PollSwapState"/> on the
    /// <c>transaction.lockupFailed</c> Boltz status, mirroring the
    /// <c>quoteSwap</c> behaviour in <c>arkade-os/boltz-swap</c>'s TS SDK.
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

            // Bound the renegotiated amount before we accept it and persist it as the
            // swap's new ExpectedAmount. A 0/negative quote is a parse/protocol bug;
            // an amount outside Boltz's chain-swap limits would be rejected when we
            // call AcceptChainQuoteAsync anyway, but checking locally avoids a wire
            // round-trip and keeps malformed values out of swap storage.
            var isBtcToArk = swap.SwapType is ArkSwapType.ChainBtcToArk;
            var limits = await _limitsValidator.GetChainLimitsAsync(isBtcToArk, ct);
            if (newQuote.Amount <= 0 ||
                (limits is not null && (newQuote.Amount < limits.MinAmount || newQuote.Amount > limits.MaxAmount)))
            {
                _logger?.LogWarning(
                    "Swap {SwapId}: rejecting renegotiated chain quote with out-of-bounds amount {Amount} sats " +
                    "(Boltz limits: min={Min}, max={Max})",
                    swap.SwapId, newQuote.Amount, limits?.MinAmount, limits?.MaxAmount);
                return false;
            }

            await _boltzClient.AcceptChainQuoteAsync(swap.SwapId, newQuote, ct);
            _logger?.LogInformation(
                "Swap {SwapId}: chain quote renegotiated — original {Original} sats → new {New} sats",
                swap.SwapId, swap.ExpectedAmount, newQuote.Amount);

            var updated = swap with
            {
                ExpectedAmount = newQuote.Amount,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _swapsStorage.SaveSwap(swap.WalletId, updated, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Boltz returns 4xx for funded amounts outside its limits — but it also
            // returns 4xx if the quote was already accepted (e.g. an overlapping
            // PollSwapState tick won the race and called AcceptChainQuoteAsync first).
            // Treating both as "refund instead" would fire a refund on a swap that
            // was just legitimately renegotiated. Disambiguate by re-reading the
            // server-side status: if Boltz has moved the swap past lockupFailed,
            // the renegotiation effectively succeeded — return true.
            try
            {
                var currentStatus = await _boltzClient.GetSwapStatusAsync(swap.SwapId, ct);
                if (currentStatus is not null &&
                    !string.IsNullOrEmpty(currentStatus.Status) &&
                    !string.Equals(currentStatus.Status, "transaction.lockupFailed", StringComparison.Ordinal))
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

            _logger?.LogWarning(ex,
                "Swap {SwapId}: chain quote renegotiation refused by Boltz", swap.SwapId);
            return false;
        }
    }
    
    
        /// <summary>
    /// Cooperative refund of an ARK→BTC chain swap whose Ark VHTLC lockup
    /// can't be redeemed (Boltz didn't lock BTC in time, swap expired,
    /// etc.). Builds an Ark refund tx spending the user's VHTLC back to a
    /// fresh receive address, asks Boltz to co-sign via
    /// <c>POST /v2/swap/chain/{id}/refund/ark</c>, submits via the existing
    /// Ark transaction builder, and marks the swap
    /// <see cref="ArkSwapStatus.Refunded"/>. Mirrors
    /// <see cref="RequestSubmarineCoopRefund"/> for submarine swaps; the
    /// only differences are the Boltz API endpoint and the swap-type guard.
    /// </summary>
    private async Task<bool> CoopRefundArkToBtcChainSwap(ArkSwap swap, CancellationToken ct)
    {
        if (swap.SwapType != ArkSwapType.ChainArkToBtc) return false;
        if (swap.Status == ArkSwapStatus.Refunded) return true;

        try
        {
            var serverInfo = await _clientTransport.GetServerInfoAsync(ct);

            var matchedSwapContracts =
                await _contractStorage.GetContracts(walletIds: [swap.WalletId], scripts: [swap.ContractScript],
                    cancellationToken: ct);
            var matchedSwapContractEntity = matchedSwapContracts.SingleOrDefault(e => e.Type == VHTLCContract.ContractType);
            if (matchedSwapContractEntity is null)
            {
                _logger?.LogWarning("Swap {SwapId}: VHTLC contract row not found for ARK→BTC refund", swap.SwapId);
                return false;
            }
            if (ArkContractParser.Parse(matchedSwapContractEntity.Type, matchedSwapContractEntity.AdditionalData,
                    serverInfo.Network) is not VHTLCContract contract)
            {
                _logger?.LogWarning("Swap {SwapId}: failed to parse VHTLC contract for ARK→BTC refund", swap.SwapId);
                return false;
            }

            // Same arkd refresh pattern the submarine refund uses — close the
            // gap between the indexer subscription stream and what arkd
            // actually has on the contract script right now.
            await foreach (var freshVtxo in _clientTransport.GetVtxoByScriptsAsSnapshot(
                               new HashSet<string> { swap.ContractScript }, ct))
            {
                await _vtxoStorage.UpsertVtxo(freshVtxo, ct);
            }

            var vtxos = await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript], cancellationToken: ct);
            if (vtxos.Count == 0)
            {
                _logger?.LogWarning("Swap {SwapId}: no VTXOs at VHTLC script for ARK→BTC refund", swap.SwapId);
                return false;
            }

            // Same multi-VTXO handling as the submarine refund path: Boltz
            // only signs the canonical lockup VTXO; extras are recovered by
            // SweeperService via the timelock path.
            var vtxo = vtxos.FirstOrDefault(v => (long)v.Amount == swap.ExpectedAmount && !v.IsSpent());
            if (vtxo is null)
            {
                _logger?.LogWarning(
                    "Swap {SwapId}: no unspent VTXO of expected amount {ExpectedAmount} at swap script (have {Total}); SweeperService will pick up extras via timelock",
                    swap.SwapId, swap.ExpectedAmount, vtxos.Count);
                return false;
            }
            if (vtxos.Count > 1)
            {
                _logger?.LogInformation(
                    "Swap {SwapId}: swap script has {Total} VTXO(s); refunding canonical {ExpectedAmount}-sat lockup, leaving {Extras} extra(s) for SweeperService",
                    swap.SwapId, vtxos.Count, swap.ExpectedAmount, vtxos.Count - 1);
            }
            var timeHeight = await _chainTimeProvider.GetChainTime(ct);
            if (!vtxo.CanSpendOffchain(timeHeight))
            {
                // CanSpendOffchain checks IsSpent || Swept || Expired — NOT the script's
                // CSV timelock. The cooperative keypath spend is fine while CSV is unmet
                // (that's its whole point). If we hit this branch the VHTLC VTXO is
                // either already spent locally, swept by arkd, or past its Arkade-level
                // expiry — in all three cases the cooperative refund can't proceed.
                _logger?.LogDebug("Swap {SwapId}: VHTLC VTXO not spendable offchain (spent/swept/expired)", swap.SwapId);
                return false;
            }

            IDestination refundAddress;
            (refundAddress, swap) = await swap.GetOrDeriveRefundDestinationAsync(
                _contractService, _swapsStorage, serverInfo.Network, ct);

            var arkCoin = contract.ToCoopRefundCoin(swap.WalletId, vtxo);

            var (arkTx, checkpoints) = await _transactionBuilder.ConstructArkTransaction(
                [arkCoin],
                [new ArkTxOut(ArkTxOutType.Vtxo, arkCoin.Amount, refundAddress)],
                serverInfo, ct);

            // ConstructArkTransaction emits exactly one checkpoint per Arkade tx input.
            // We pass a single ArkCoin, so the checkpoint list must have length 1; a
            // mismatch indicates a protocol/SDK change rather than a recoverable error,
            // so surface it with an actionable message instead of a bare Single() throw.
            if (checkpoints.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Swap {swap.SwapId}: expected exactly 1 checkpoint for a single-input ARK→BTC refund, " +
                    $"got {checkpoints.Count}. Protocol invariant violated or SDK out of sync.");
            }
            var checkpoint = checkpoints.First();

            var refundResponse = await _boltzClient.RefundChainSwapArkAsync(swap.SwapId,
                new ChainArkRefundRequest
                {
                    Transaction = arkTx.ToBase64(),
                    Checkpoint = checkpoint.Psbt.ToBase64(),
                }, ct);

            var boltzSignedRefundPsbt = PSBT.Parse(refundResponse.Transaction, serverInfo.Network);
            var boltzSignedCheckpointPsbt = PSBT.Parse(refundResponse.Checkpoint, serverInfo.Network);
            arkTx.UpdateFrom(boltzSignedRefundPsbt);
            checkpoint.Psbt.UpdateFrom(boltzSignedCheckpointPsbt);

            await _transactionBuilder.SubmitArkTransaction([arkCoin], arkTx, [checkpoint], ct);

            var refunded = swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.UtcNow };
            await _swapsStorage.SaveSwap(swap.WalletId, refunded, ct);
            RaiseSwapStatusChanged(refunded);
            _logger?.LogInformation("Swap {SwapId}: ARK→BTC cooperative refund completed", swap.SwapId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Swap {SwapId}: ARK→BTC cooperative refund failed", swap.SwapId);
            return false;
        }
    }

    /// <summary>
    /// Cooperative refund of a BTC→ARK chain swap whose user-funded BTC
    /// lockup couldn't be redeemed (renegotiation refused, swap expired,
    /// etc.). Asks Boltz for a MuSig2 partial signature on a refund tx that
    /// spends the lockup back to the user's stored BTC destination, signs
    /// our half, broadcasts via Boltz's BTC broadcaster, and marks the
    /// swap <see cref="ArkSwapStatus.Refunded"/>. Returns <c>false</c> on
    /// any failure (Boltz refuses, lockup tx not yet observable, etc.) so
    /// the routine poll loop will retry on the next tick.
    /// </summary>
    /// <remarks>
    /// The signing primitive (<see cref="ChainSwapMusigSession.CooperativeRefundAsync"/>)
    /// already existed; this method wires the lookup of the lockup tx,
    /// outpoint discovery, refund-tx construction, and broadcast around it.
    /// Mirrors the symmetry of <see cref="TryClaimBtcForChainSwap"/>.
    /// </remarks>
    private async Task<bool> CoopRefundBtcToArkChainSwap(ArkSwap swap, CancellationToken ct)
    {
        if (swap.SwapType != ArkSwapType.ChainBtcToArk) return false;
        if (swap.Status == ArkSwapStatus.Refunded) return true;

        var ephemeralKeyHex = swap.Get(SwapMetadata.EphemeralKey);
        var boltzResponseJson = swap.Get(SwapMetadata.BoltzResponse);
        var btcAddress = swap.Get(SwapMetadata.BtcAddress);

        if (string.IsNullOrEmpty(ephemeralKeyHex) ||
            string.IsNullOrEmpty(boltzResponseJson) ||
            string.IsNullOrEmpty(btcAddress))
        {
            _logger?.LogWarning("Swap {SwapId}: missing chain-swap metadata for BTC refund", swap.SwapId);
            return false;
        }

        try
        {
            var response = BoltzSwapService.DeserializeChainResponse(boltzResponseJson);
            // For BTC→ARK refund the lockup is on BTC — held by `lockupDetails`,
            // not `claimDetails` (claimDetails is for the Ark side which Boltz
            // is going to reverse). Refund spends the user's BTC lockup back
            // to the user-supplied refund destination.
            var lockupDetails = response?.LockupDetails;
            if (lockupDetails?.SwapTree is null || string.IsNullOrEmpty(lockupDetails.ServerPublicKey))
            {
                _logger?.LogWarning("Swap {SwapId}: BTC lockup details missing from Boltz response, can't refund", swap.SwapId);
                return false;
            }

            var ephemeralKey = new Key(Convert.FromHexString(ephemeralKeyHex));
            var ecPrivKey = ECPrivKey.Create(ephemeralKey.ToBytes());
            var userPubKey = ecPrivKey.CreatePubKey();
            var boltzPubKey = ECPubKey.Create(Convert.FromHexString(lockupDetails.ServerPublicKey));

            var serverInfo = await _clientTransport.GetServerInfoAsync(ct);
            var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                lockupDetails.SwapTree, userPubKey, boltzPubKey,
                lockupDetails.LockupAddress, serverInfo.Network);
            var refundDest = BitcoinAddress.Create(btcAddress, serverInfo.Network);

            var swapStatus = await _boltzClient.GetSwapStatusAsync(swap.SwapId, ct);
            if (string.IsNullOrEmpty(swapStatus?.Transaction?.Hex))
            {
                _logger?.LogDebug("Swap {SwapId}: BTC lockup tx not yet observable from Boltz, deferring refund", swap.SwapId);
                return false;
            }

            var lockupTx = Transaction.Parse(swapStatus.Transaction.Hex, serverInfo.Network);
            var lockupScript = BitcoinAddress.Create(lockupDetails.LockupAddress, serverInfo.Network).ScriptPubKey;
            var vout = -1;
            for (var i = 0; i < lockupTx.Outputs.Count; i++)
            {
                if (lockupTx.Outputs[i].ScriptPubKey == lockupScript) { vout = i; break; }
            }
            if (vout < 0)
            {
                _logger?.LogWarning("Swap {SwapId}: lockup tx has no output paying to {Address}", swap.SwapId, lockupDetails.LockupAddress);
                return false;
            }

            var outpoint = new OutPoint(lockupTx.GetHash(), vout);
            var prevOut = lockupTx.Outputs[vout];

            // Same flat fee as TryClaimBtcForChainSwap — see DefaultRefundClaimFeeSats
            // for the rationale + TODO to plumb in IFeeEstimator.
            var unsignedRefundTx = BtcTransactionBuilder.BuildKeyPathClaimTx(outpoint, prevOut, refundDest, DefaultRefundClaimFeeSats);

            _logger?.LogInformation("Swap {SwapId}: requesting MuSig2 cooperative BTC refund", swap.SwapId);
            var signedTx = await _chainSwapMusig.CooperativeRefundAsync(
                swap.SwapId, unsignedRefundTx, prevOut, inputIndex: 0,
                ecPrivKey, boltzPubKey, spendInfo, ct);

            var broadcastResult = await _boltzClient.BroadcastBtcTransactionAsync(
                new BroadcastRequest { Hex = signedTx.ToHex() }, ct);
            _logger?.LogInformation("Swap {SwapId}: BTC refund broadcast — txid={TxId}", swap.SwapId, broadcastResult.Id);

            var refunded = swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.UtcNow };
            await _swapsStorage.SaveSwap(swap.WalletId, refunded, ct);
            RaiseSwapStatusChanged(refunded);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Swap {SwapId}: BTC cooperative refund failed", swap.SwapId);
            return false;
        }
    }

    
    /// <summary>
    /// Chain swap cooperative refund — only on swap.expired.
    /// 
    /// User locked ARK in a VHTLC; we cooperatively
    /// spend it back via POST /v2/swap/chain/{id}/refund/ark.
    /// </summary>
    public async Task<bool> TryCoopRefundArkToBtc(ArkSwap swap, SwapStatusResponse swapStatus, CancellationToken cancellationToken)
    {
        _logger?.LogInformation(
            "Swap {SwapId}: chain swap expired ({SwapType}), attempting cooperative refund",
            swap.SwapId, swap.SwapType);
        
        if (await CoopRefundArkToBtcChainSwap(swap, cancellationToken))
        {
            return true;
        }

        // Coop failed — fall back to unilateral ARK exit if UnilateralExitService is registered.
        if (await TryStartVhtlcUnilateralExitAsync(swap, cancellationToken))
        {
            return true;
        }

        // Nothing to recover — mark Failed so the poll stops retrying.
        var vtxosLocked = await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript], cancellationToken: cancellationToken);
        if (vtxosLocked.Count == 0 && swap.Status != ArkSwapStatus.Failed)
        {
            _logger?.LogInformation(
                "Swap {SwapId}: expired with no observable lockup — marking Failed",
                swap.SwapId);
            var failedSwap = swap with
            {
                Status = ArkSwapStatus.Failed,
                FailReason = "Swap expired before any funds were locked",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _swapsStorage.SaveSwap(swap.WalletId, failedSwap, cancellationToken);
            RaiseSwapStatusChanged(failedSwap, failedSwap.FailReason);
        }

        return false;
    }

    
    /// <summary>
    /// Chain swap cooperative refund — only on swap.expired.
    /// 
    /// BTC→ARK (from=BTC): the BTC lockup is refunded on-chain by Boltz
    /// after the timelock elapses — there is no client-side action.
    /// Per arkade-os/boltz-swap TS SDK: "BTC-side lockup refunds are
    /// handled on-chain by Boltz after the timelock expires."
    /// We attempt a MuSig2 cooperative refund as an optimisation (saves
    /// the user from waiting for the full timelock); if Boltz refuses
    /// (e.g. the lockup tx isn't visible yet) we leave the swap Pending
    /// so the routine poll retries.
    /// </summary>
    public async Task<bool> TryRefundBtcToArk(ArkSwap swap, SwapStatusResponse swapStatus, CancellationToken cancellationToken)
    {
        
        _logger?.LogInformation(
            "Swap {SwapId}: chain swap expired ({SwapType}), attempting cooperative refund",
            swap.SwapId, swap.SwapType);

        if (await CoopRefundBtcToArkChainSwap(swap, cancellationToken))
        {
            return true;
        }

        // Coop refused — fall through to unilateral script-path spend once CLTV timeout is reached.
        if (await UnilateralRefundBtcToArkChainSwap(swap, cancellationToken))
        {
            return true;
        }

        var noBtcLockup = string.IsNullOrEmpty(swapStatus.Transaction?.Hex);
        if (noBtcLockup && swap.Status != ArkSwapStatus.Failed)
        {
            _logger?.LogInformation(
                "Swap {SwapId}: expired with no observable lockup — marking Failed",
                swap.SwapId);
            var failedSwap = swap with
            {
                Status = ArkSwapStatus.Failed,
                    FailReason = "Swap expired before any funds were locked",
                    UpdatedAt = DateTimeOffset.UtcNow
            };
            await _swapsStorage.SaveSwap(swap.WalletId, failedSwap, cancellationToken);
            RaiseSwapStatusChanged(failedSwap, failedSwap.FailReason);
        }
        return false;
    }
    
    
    /// <summary>
    /// Script-path unilateral CLTV refund for a BTC→ARK chain swap whose BTC
    /// lockup was never redeemed and cooperative refund was refused by Boltz.
    /// Spends the BTC HTLC output via the refund tapscript leaf once the CLTV
    /// timelock (<c>lockupDetails.TimeoutBlockHeight</c>) has elapsed.
    /// Returns <c>false</c> until the timeout is reached so the poll loop
    /// continues retrying on each tick.
    /// </summary>
    private async Task<bool> UnilateralRefundBtcToArkChainSwap(ArkSwap swap, CancellationToken ct)
    {
        if (swap.SwapType != ArkSwapType.ChainBtcToArk) return false;
        if (swap.Status == ArkSwapStatus.Refunded) return true;

        var ephemeralKeyHex = swap.Get(SwapMetadata.EphemeralKey);
        var boltzResponseJson = swap.Get(SwapMetadata.BoltzResponse);
        var btcAddress = swap.Get(SwapMetadata.BtcAddress);
        if (string.IsNullOrEmpty(ephemeralKeyHex) ||
            string.IsNullOrEmpty(boltzResponseJson) ||
            string.IsNullOrEmpty(btcAddress))
        {
            _logger?.LogWarning("Swap {SwapId}: missing metadata for unilateral BTC refund", swap.SwapId);
            return false;
        }

        try
        {
            var response = BoltzSwapService.DeserializeChainResponse(boltzResponseJson);
            var lockupDetails = response?.LockupDetails;
            if (lockupDetails?.SwapTree is null || string.IsNullOrEmpty(lockupDetails.ServerPublicKey))
            {
                _logger?.LogWarning("Swap {SwapId}: BTC lockup details missing, cannot unilateral refund", swap.SwapId);
                return false;
            }

            // CLTV timeout is provided directly by Boltz — no script parsing needed.
            var cltvTimeout = (uint)lockupDetails.TimeoutBlockHeight;
            var chainTime = await _chainTimeProvider.GetChainTime(ct);
            if (chainTime.Height < cltvTimeout)
            {
                _logger?.LogDebug(
                    "Swap {SwapId}: CLTV timeout {Timeout} not yet reached (height={Height}), deferring unilateral refund",
                    swap.SwapId, cltvTimeout, chainTime.Height);
                return false;
            }

            var swapStatus = await _boltzClient.GetSwapStatusAsync(swap.SwapId, ct);
            if (string.IsNullOrEmpty(swapStatus?.Transaction?.Hex))
            {
                _logger?.LogDebug("Swap {SwapId}: BTC lockup tx not yet observable, deferring unilateral refund", swap.SwapId);
                return false;
            }

            var serverInfo = await _clientTransport.GetServerInfoAsync(ct);
            var ephemeralKey = new Key(Convert.FromHexString(ephemeralKeyHex));
            var ecPrivKey = ECPrivKey.Create(ephemeralKey.ToBytes());
            var boltzPubKey = ECPubKey.Create(Convert.FromHexString(lockupDetails.ServerPublicKey));

            var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                lockupDetails.SwapTree, ecPrivKey.CreatePubKey(), boltzPubKey,
                lockupDetails.LockupAddress, serverInfo.Network);
            var refundLeaf = BtcHtlcScripts.GetRefundLeaf(lockupDetails.SwapTree);
            var refundDest = BitcoinAddress.Create(btcAddress, serverInfo.Network);

            var lockupTx = Transaction.Parse(swapStatus.Transaction.Hex, serverInfo.Network);
            var lockupScript = BitcoinAddress.Create(lockupDetails.LockupAddress, serverInfo.Network).ScriptPubKey;
            var vout = -1;
            for (var i = 0; i < lockupTx.Outputs.Count; i++)
            {
                if (lockupTx.Outputs[i].ScriptPubKey == lockupScript) { vout = i; break; }
            }
            if (vout < 0)
            {
                _logger?.LogWarning("Swap {SwapId}: lockup tx has no output paying to {Address}", swap.SwapId, lockupDetails.LockupAddress);
                return false;
            }

            var outpoint = new OutPoint(lockupTx.GetHash(), vout);
            var prevOut = lockupTx.Outputs[vout];
            var refundTx = BtcTransactionBuilder.BuildKeyPathClaimTx(outpoint, prevOut, refundDest, DefaultRefundClaimFeeSats);
            BtcTransactionBuilder.SignScriptPathRefund(refundTx, 0, prevOut, spendInfo, refundLeaf, cltvTimeout, ephemeralKey);

            _logger?.LogInformation(
                "Swap {SwapId}: broadcasting unilateral script-path BTC refund (CLTV={Timeout})",
                swap.SwapId, cltvTimeout);
            var broadcastResult = await _boltzClient.BroadcastBtcTransactionAsync(
                new BroadcastRequest { Hex = refundTx.ToHex() }, ct);
            _logger?.LogInformation("Swap {SwapId}: unilateral BTC refund broadcast — txid={TxId}",
                swap.SwapId, broadcastResult.Id);

            var refunded = swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.UtcNow };
            await _swapsStorage.SaveSwap(swap.WalletId, refunded, ct);
            RaiseSwapStatusChanged(refunded);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Swap {SwapId}: unilateral BTC refund failed", swap.SwapId);
            return false;
        }
    }

    /// <summary>
    /// When Boltz permanently refuses cooperative refund for an ARK-side VTXO
    /// (submarine or ARK→BTC chain swap), triggers a unilateral exit: broadcasts
    /// the virtual tx tree to Bitcoin L1 and creates an <see cref="ExitSession"/>
    /// that <c>UnilateralExitService.ProgressExitsAsync</c> advances through the
    /// CSV wait and final claim. Returns <c>false</c> if
    /// <see cref="_unilateralExitService"/> is not registered, no unspent VTXO
    /// exists, or claim address derivation fails.
    /// </summary>
    private async Task<bool> TryStartVhtlcUnilateralExitAsync(ArkSwap swap, CancellationToken ct)
    {
        if (_unilateralExitService is null) return false;

        var vtxos = await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript], cancellationToken: ct);
        var vtxo = vtxos.FirstOrDefault(v => (long)v.Amount == swap.ExpectedAmount && !v.IsSpent());
        if (vtxo is null)
        {
            _logger?.LogDebug("Swap {SwapId}: no unspent canonical VTXO — skipping unilateral exit", swap.SwapId);
            return false;
        }

        var claimAddress = await DeriveExitClaimAddressAsync(swap.WalletId, ct);
        if (claimAddress is null) return false;

        _logger?.LogWarning(
            "Swap {SwapId}: cooperative refund exhausted, starting unilateral ARK exit for VTXO {Outpoint} → {Claim}",
            swap.SwapId, vtxo.OutPoint, claimAddress);
        await _unilateralExitService.StartExitAsync(swap.WalletId, [vtxo.OutPoint], claimAddress, ct);

        // Mark the swap Refunded — the exit session in UnilateralExitService now owns recovery.
        var refunded = swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.UtcNow };
        await _swapsStorage.SaveSwap(swap.WalletId, refunded, ct);
        RaiseSwapStatusChanged(refunded);
        return true;
    }

    /// <summary>
    /// Derives a P2TR Bitcoin address for the unilateral exit claim output.
    /// Prefers a boarding contract so recovered funds can re-enter Arkade
    /// without a separate onboarding step. Mirrors the identical helper in
    /// <c>ExitWatchtowerService</c>.
    /// </summary>
    private async Task<BitcoinAddress?> DeriveExitClaimAddressAsync(string walletId, CancellationToken ct)
    {
        try
        {
            var serverInfo = await _clientTransport.GetServerInfoAsync(ct);
            var contract = await _contractService.DeriveContract(
                walletId, NextContractPurpose.Boarding, ContractActivityState.Active, cancellationToken: ct);
            if (contract is ArkBoardingContract boarding)
                return boarding.GetOnchainAddress(serverInfo.Network);
            var spendInfo = contract.GetTaprootSpendInfo();
            return spendInfo.OutputPubKey.GetAddress(serverInfo.Network);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to derive exit claim address for wallet {WalletId}", walletId);
            return null;
        }
    }
}