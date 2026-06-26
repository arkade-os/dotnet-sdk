using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Exit;
using NArk.Abstractions.VirtualTxs;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Exit;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Services;

/// <summary>
/// Orchestrates unilateral exit for VTXOs. Broadcasts the chain of virtual txs
/// from commitment to leaf, waits for CSV timelock, then claims funds on-chain.
/// </summary>
public class UnilateralExitService(
    IClientTransport transport,
    IVirtualTxStorage virtualTxStorage,
    IExitSessionStorage exitSessionStorage,
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IBitcoinBlockchain blockchain,
    IWalletProvider walletProvider,
    VirtualTxService virtualTxService,
    IFeeWallet? feeWallet = null,
    ILogger<UnilateralExitService>? logger = null)
{
    private const int MaxBroadcastRetries = 10;

    /// <summary>
    /// Start unilateral exit for specific VTXOs.
    /// </summary>
    public async Task<IReadOnlyList<ExitSession>> StartExitAsync(
        string walletId,
        IReadOnlyCollection<OutPoint> vtxoOutpoints,
        BitcoinAddress claimAddress,
        CancellationToken cancellationToken = default)
    {
        var sessions = new List<ExitSession>();

        foreach (var outpoint in vtxoOutpoints)
        {
            // Check if session already exists
            var existing = await exitSessionStorage.GetByVtxoAsync(outpoint, cancellationToken);
            if (existing is not null)
            {
                logger?.LogWarning("Exit session already exists for VTXO {Outpoint}, state={State}",
                    outpoint, existing.State);
                sessions.Add(existing);
                continue;
            }

            // Ensure virtual tx hex is populated
            await virtualTxService.EnsureHexPopulatedAsync(outpoint, cancellationToken);

            // Verify branch exists and has hex
            var branch = await virtualTxStorage.GetBranchAsync(outpoint, cancellationToken);
            if (branch.Count == 0)
            {
                logger?.LogError("No virtual tx branch found for VTXO {Outpoint}, cannot start exit", outpoint);
                continue;
            }

            // Commitment txs are on-chain anchors; arkd never serves hex
            // for them via GetVirtualTxs, so a null hex on a Commitment
            // row is expected. Only the off-chain rows (Tree / Ark /
            // Checkpoint) need hex to be broadcastable.
            var missingHex = branch.Any(tx =>
                tx.Hex is null && tx.Type != ChainedTxType.Commitment);
            if (missingHex)
            {
                logger?.LogError("Virtual tx branch for VTXO {Outpoint} has missing hex, cannot start exit", outpoint);
                continue;
            }

            var session = new ExitSession(
                Id: Guid.NewGuid().ToString(),
                VtxoTxid: outpoint.Hash.ToString(),
                VtxoVout: outpoint.N,
                WalletId: walletId,
                ClaimAddress: claimAddress.ToString(),
                State: ExitSessionState.Broadcasting,
                NextTxIndex: 0,
                ClaimTxid: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                FailReason: null);

            await exitSessionStorage.UpsertAsync(session, cancellationToken);
            sessions.Add(session);

            logger?.LogInformation("Started unilateral exit for VTXO {Outpoint}, session={SessionId}",
                outpoint, session.Id);
        }

        return sessions;
    }

    /// <summary>
    /// Start unilateral exit for all unspent VTXOs in a wallet.
    /// </summary>
    public async Task<IReadOnlyList<ExitSession>> StartExitForWalletAsync(
        string walletId,
        BitcoinAddress claimAddress,
        CancellationToken cancellationToken = default)
    {
        // Get all contracts for wallet
        var contracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            isActive: true,
            cancellationToken: cancellationToken);

        var scripts = contracts.Select(c => c.Script).ToArray();
        var vtxos = await vtxoStorage.GetVtxos(scripts: scripts, cancellationToken: cancellationToken);
        var unspent = vtxos.Where(v => !v.IsSpent()).ToList();

        var outpoints = unspent.Select(v => v.OutPoint).ToList();
        return await StartExitAsync(walletId, outpoints, claimAddress, cancellationToken);
    }

    /// <summary>
    /// Progress all active exit sessions. Call this periodically.
    /// Advances sessions through: Broadcasting → AwaitingCsvDelay → Claimable → Claiming → Completed.
    /// </summary>
    public async Task ProgressExitsAsync(CancellationToken cancellationToken = default)
    {
        // Process Broadcasting sessions
        var broadcasting = await exitSessionStorage.GetByStateAsync(
            ExitSessionState.Broadcasting, cancellationToken);
        foreach (var session in broadcasting)
        {
            await ProgressBroadcastingAsync(session, cancellationToken);
        }

        // Process AwaitingCsvDelay sessions
        var awaiting = await exitSessionStorage.GetByStateAsync(
            ExitSessionState.AwaitingCsvDelay, cancellationToken);
        foreach (var session in awaiting)
        {
            await ProgressAwaitingCsvAsync(session, cancellationToken);
        }

        // Process Claimable sessions
        var claimable = await exitSessionStorage.GetByStateAsync(
            ExitSessionState.Claimable, cancellationToken);
        foreach (var session in claimable)
        {
            await ProgressClaimableAsync(session, cancellationToken);
        }

        // Process Claiming sessions
        var claiming = await exitSessionStorage.GetByStateAsync(
            ExitSessionState.Claiming, cancellationToken);
        foreach (var session in claiming)
        {
            await ProgressClaimingAsync(session, cancellationToken);
        }
    }

    /// <summary>
    /// Get current exit sessions, optionally filtered by wallet.
    /// </summary>
    public Task<IReadOnlyList<ExitSession>> GetActiveSessionsAsync(
        string? walletId = null,
        CancellationToken cancellationToken = default)
        => exitSessionStorage.GetActiveSessionsAsync(walletId, cancellationToken);

    // ── Stateless exit API ─────────────────────────────────────────────
    //
    // The two methods below are an alternative to StartExitAsync +
    // ProgressExitsAsync that don't touch IExitSessionStorage or
    // IVirtualTxStorage. They fetch the chain from arkd on each call,
    // hold it in memory long enough to broadcast / claim, and return
    // a small ExitPlan record the caller persists however they want.
    //
    // Trade-off: no idempotency (a second BroadcastExitChainAsync call
    // will re-broadcast), no automatic watchtower progression, no resume
    // across restarts. Gain: zero exit-specific persistence cost.

    /// <summary>
    /// One-shot, stateless equivalent of <see cref="StartExitAsync"/> +
    /// the Broadcasting phase of <see cref="ProgressExitsAsync"/>: fetches the
    /// virtual-tx chain from arkd, broadcasts every off-chain row that isn't
    /// already on-chain, and returns an <see cref="ExitPlan"/> the caller
    /// persists in whatever form they prefer.
    /// </summary>
    /// <remarks>
    /// The SDK doesn't write anything exit-specific in this call —
    /// <see cref="IExitSessionStorage"/> and <see cref="IVirtualTxStorage"/>
    /// are not touched. Once the leaf-tx confirms on-chain and the CSV
    /// timelock has matured, feed the returned <see cref="ExitPlan"/> back
    /// to <see cref="ClaimMaturedExitAsync"/> to finalise the exit.
    /// </remarks>
    public async Task<ExitPlan> BroadcastExitChainAsync(
        string walletId,
        OutPoint vtxoOutpoint,
        BitcoinAddress claimAddress,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

        // 1. Fetch the chain fresh from arkd — no virtualTxStorage hit.
        var chainEntries = await transport.GetVtxoChainAsync(vtxoOutpoint, cancellationToken);
        if (chainEntries.Count == 0)
            throw new InvalidOperationException(
                $"arkd returned an empty virtual-tx chain for VTXO {vtxoOutpoint}; " +
                "the VTXO may not exist or may already be unrolled.");

        // 2. Fetch hex for off-chain rows only (Commitment is on-chain;
        //    arkd's GetVirtualTxs doesn't serve it).
        var offChainTxids = chainEntries
            .Where(e => e.Type is ChainedTxType.Tree or ChainedTxType.Ark or ChainedTxType.Checkpoint)
            .Select(e => e.Txid)
            .ToList();
        var hexList = offChainTxids.Count > 0
            ? await transport.GetVirtualTxsAsync(offChainTxids, cancellationToken)
            : [];
        if (hexList.Count != offChainTxids.Count)
            throw new InvalidOperationException(
                $"Virtual-tx hex count mismatch for VTXO {vtxoOutpoint}: " +
                $"expected {offChainTxids.Count}, got {hexList.Count}");

        var hexByTxid = offChainTxids
            .Zip(hexList, (id, hex) => (id, hex))
            .ToDictionary(t => t.id, t => t.hex);

        // 3. Walk the chain root→leaf, broadcast each off-chain row that
        //    isn't already in mempool / confirmed. Commitment rows are
        //    skipped (already on-chain by the operator at batch finalize).
        string? leafTxid = null;
        foreach (var entry in chainEntries)
        {
            if (entry.Type == ChainedTxType.Commitment)
                continue;

            leafTxid = entry.Txid;
            if (!hexByTxid.TryGetValue(entry.Txid, out var hex))
                throw new InvalidOperationException(
                    $"Missing hex for virtual tx {entry.Txid} in chain of VTXO {vtxoOutpoint}");

            var txid = uint256.Parse(entry.Txid);
            var status = await blockchain.GetTxStatusAsync(txid, cancellationToken);
            if (status.Confirmed || status.InMempool)
            {
                logger?.LogDebug(
                    "Stateless exit: virtual tx {Txid} already {Status}, skipping broadcast",
                    entry.Txid, status.Confirmed ? "confirmed" : "in mempool");
                continue;
            }

            var tx = ParseVirtualTx(hex, serverInfo.Network, entry.Type);
            var success = await BroadcastWithCpfpAsync(tx, cancellationToken);
            if (!success)
                throw new InvalidOperationException(
                    $"Failed to broadcast virtual tx {entry.Txid} for VTXO {vtxoOutpoint}");

            logger?.LogInformation(
                "Stateless exit: broadcast virtual tx {Txid} for VTXO {Outpoint}",
                entry.Txid, vtxoOutpoint);
        }

        if (leafTxid is null)
            throw new InvalidOperationException(
                $"Virtual-tx chain for VTXO {vtxoOutpoint} contained no off-chain rows " +
                "— nothing to broadcast.");

        return new ExitPlan(
            WalletId: walletId,
            VtxoTxid: vtxoOutpoint.Hash.ToString(),
            VtxoVout: vtxoOutpoint.N,
            ClaimAddress: claimAddress.ToString(),
            LeafTxid: leafTxid,
            CsvDelay: (int)serverInfo.UnilateralExit.Value);
    }

    /// <summary>
    /// Stateless counterpart to the Claimable phase of
    /// <see cref="ProgressExitsAsync"/>. Verifies the leaf tx referenced by
    /// <paramref name="plan"/> is confirmed and that the CSV timelock has
    /// matured, then builds, signs, and broadcasts the claim transaction.
    /// </summary>
    /// <returns>
    /// Txid of the broadcast claim transaction, or <c>null</c> when CSV
    /// hasn't matured yet (caller should poll again later).
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// VTXO / contract / signer state required to build the claim is
    /// unavailable or the leaf tx is not yet confirmed at all (callers should
    /// distinguish "not yet" from a hard failure via the message).
    /// </exception>
    public async Task<string?> ClaimMaturedExitAsync(
        ExitPlan plan,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

        // 1. Verify leaf is confirmed.
        var leafStatus = await blockchain.GetTxStatusAsync(
            uint256.Parse(plan.LeafTxid), cancellationToken);
        if (!leafStatus.Confirmed || leafStatus.BlockHeight is null)
            throw new InvalidOperationException(
                $"Leaf tx {plan.LeafTxid} is not yet confirmed; cannot claim. " +
                "Wait for confirmation and retry.");

        // 2. Verify CSV matured.
        var chainTime = await blockchain.GetChainTime(cancellationToken);
        var matureAt = leafStatus.BlockHeight.Value + plan.CsvDelay;
        if (chainTime.Height < matureAt)
        {
            logger?.LogDebug(
                "Stateless claim: CSV not yet matured for VTXO {Txid}:{Vout} " +
                "({Current}/{Matures})",
                plan.VtxoTxid, plan.VtxoVout, chainTime.Height, matureAt);
            return null;
        }

        // 3. Re-derive everything else from live wallet state.
        var vtxoOutpoint = new OutPoint(uint256.Parse(plan.VtxoTxid), plan.VtxoVout);
        var vtxos = await vtxoStorage.GetVtxos(outpoints: [vtxoOutpoint], cancellationToken: cancellationToken);
        var vtxo = vtxos.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"VTXO {vtxoOutpoint} not found in storage; cannot build claim.");

        var contracts = await contractStorage.GetContracts(scripts: [vtxo.Script], cancellationToken: cancellationToken);
        var contractEntity = contracts.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Contract not found for VTXO {vtxoOutpoint} (script {vtxo.Script}).");

        var contract = ArkContractParser.Parse(contractEntity.Type, contractEntity.AdditionalData, serverInfo.Network)
            ?? throw new InvalidOperationException(
                $"Failed to parse contract for VTXO {vtxoOutpoint}.");

        var unilateralTapScript = GetUnilateralPathTapScript(contract)
            ?? throw new InvalidOperationException(
                $"Contract for VTXO {vtxoOutpoint} does not support unilateral exit.");

        var tapScript = unilateralTapScript.Build();
        var spendInfo = contract.GetTaprootSpendInfo();
        var controlBlock = spendInfo.GetControlBlock(tapScript);

        var signer = await walletProvider.GetSignerAsync(plan.WalletId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No signer registered for wallet {plan.WalletId}.");

        // 4. Find the VTXO output in the leaf tx. Refetch hex for the leaf
        //    from arkd since we didn't persist it — same primitive the
        //    autofetch / EnsureHexPopulated path uses.
        var leafHexList = await transport.GetVirtualTxsAsync([plan.LeafTxid], cancellationToken);
        if (leafHexList.Count != 1)
            throw new InvalidOperationException(
                $"arkd didn't return hex for leaf tx {plan.LeafTxid}; cannot build claim.");

        var leafChainType = (await transport.GetVtxoChainAsync(vtxoOutpoint, cancellationToken))
            .LastOrDefault(e => e.Type is ChainedTxType.Tree or ChainedTxType.Ark or ChainedTxType.Checkpoint)?.Type
            ?? ChainedTxType.Unspecified;
        var parsedLeafTx = ParseVirtualTx(leafHexList[0], serverInfo.Network, leafChainType);
        var vtxoTxOut = parsedLeafTx.Outputs.AsIndexedOutputs()
            .FirstOrDefault(o => o.TxOut.ScriptPubKey.ToHex() == vtxo.Script)
            ?? throw new InvalidOperationException(
                $"VTXO output {vtxoOutpoint} not present in leaf tx {plan.LeafTxid}.");

        // 5. Build, sign, broadcast claim.
        var claimAddress = BitcoinAddress.Create(plan.ClaimAddress, serverInfo.Network);
        var feeRate = await blockchain.EstimateFeeRateAsync(6, cancellationToken);
        var claimTx = Helpers.TransactionHelpers.BuildCsvSpendTransaction(
            vtxoOutpoint, vtxoTxOut.TxOut, claimAddress, serverInfo.UnilateralExit,
            tapScript, controlBlock, feeRate, serverInfo.Network);

        var precomputed = claimTx.PrecomputeTransactionData([vtxoTxOut.TxOut]);
        var sighash = claimTx.GetSignatureHashTaproot(
            precomputed,
            new TaprootExecutionData(0, tapScript.LeafHash) { SigHash = TaprootSigHash.Default });

        if (!contractEntity.AdditionalData.TryGetValue("user", out var userDesc))
            throw new InvalidOperationException(
                "User descriptor missing from contract data; cannot sign claim.");
        var descriptor = Abstractions.Extensions.KeyExtensions.ParseOutputDescriptor(userDesc, serverInfo.Network);

        var (_, sig) = await signer.Sign(descriptor, sighash, cancellationToken);
        claimTx.Inputs[0].WitScript = new WitScript(
            [sig.ToBytes(), tapScript.Script.ToBytes(), controlBlock.ToBytes()], true);

        var success = await blockchain.BroadcastAsync(claimTx, cancellationToken);
        if (!success)
            throw new InvalidOperationException(
                $"Failed to broadcast claim tx for VTXO {vtxoOutpoint}.");

        var claimTxid = claimTx.GetHash().ToString();
        logger?.LogInformation(
            "Stateless exit: broadcast claim tx {ClaimTxid} for VTXO {Outpoint}",
            claimTxid, vtxoOutpoint);
        return claimTxid;
    }

    private async Task ProgressBroadcastingAsync(ExitSession session, CancellationToken ct)
    {
        try
        {
            var vtxoOutpoint = new OutPoint(uint256.Parse(session.VtxoTxid), session.VtxoVout);
            var branch = await virtualTxStorage.GetBranchAsync(vtxoOutpoint, ct);

            if (branch.Count == 0)
            {
                await FailSession(session, "Virtual tx branch not found", ct);
                return;
            }

            var serverInfo = await transport.GetServerInfoAsync(ct);
            var network = serverInfo.Network;

            // Process from NextTxIndex onwards
            for (var i = session.NextTxIndex; i < branch.Count; i++)
            {
                var vtx = branch[i];

                // Commitment is the on-chain anchor — already published by
                // the operator at batch finalize. Nothing to broadcast for
                // it, just verify and move on.
                if (vtx.Type == ChainedTxType.Commitment)
                {
                    logger?.LogDebug("Skipping commitment-tx {Txid} (already on-chain)", vtx.Txid);
                    continue;
                }

                if (vtx.Hex is null)
                {
                    await FailSession(session, $"Missing hex for virtual tx {vtx.Txid}", ct);
                    return;
                }

                var txid = uint256.Parse(vtx.Txid);

                // Check if already confirmed
                var status = await blockchain.GetTxStatusAsync(txid, ct);
                if (status.Confirmed)
                {
                    logger?.LogDebug("Virtual tx {Txid} already confirmed at height {Height}",
                        vtx.Txid, status.BlockHeight);
                    continue;
                }

                // Check if in mempool
                if (status.InMempool)
                {
                    logger?.LogDebug("Virtual tx {Txid} in mempool, waiting for confirmation", vtx.Txid);
                    await UpdateSession(session with
                    {
                        NextTxIndex = i,
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, ct);
                    return;
                }

                // Not seen — broadcast. arkd's GetVirtualTxs returns the
                // tree txs as PSBT-encoded strings (the same format that
                // BatchSession parses across the rest of the codebase) —
                // not raw consensus-encoded transactions. Parse + extract.
                Transaction tx;
                try
                {
                    tx = ParseVirtualTx(vtx.Hex, network, vtx.Type);
                }
                catch (TreeTxNotSignedException ex)
                {
                    // Preconfirmed VTXO: arkd hasn't co-signed the tree yet (the batch round
                    // hasn't fired). Re-fetch the branch — the stored copy may have been cached
                    // while the VTXO was preconfirmed and is therefore unsigned — then leave the
                    // session in Broadcasting and retry on the next poll, instead of failing.
                    // Mirrors go-sdk's ErrWaitingForConfirmation retry loop.
                    logger?.LogInformation(
                        "Tree tx {Txid} not yet co-signed for session {SessionId}; refreshing branch and waiting for the batch round ({Reason})",
                        vtx.Txid, session.Id, ex.Message);
                    await virtualTxService.FetchAndStoreBranchAsync(
                        vtxoOutpoint, VirtualTxMode.Full, forceRefresh: true, ct);
                    await UpdateSession(session with
                    {
                        NextTxIndex = i,
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, ct);
                    return;
                }
                var success = await BroadcastWithCpfpAsync(tx, ct);

                if (!success)
                {
                    var retries = session.RetryCount + 1;
                    logger?.LogWarning(
                        "Failed to broadcast virtual tx {Txid} for session {SessionId} (retry {Retry}/{Max})",
                        vtx.Txid, session.Id, retries, MaxBroadcastRetries);

                    if (retries >= MaxBroadcastRetries)
                    {
                        await FailSession(session,
                            $"Exceeded {MaxBroadcastRetries} broadcast retries for tx {vtx.Txid}", ct);
                        return;
                    }

                    await UpdateSession(session with
                    {
                        NextTxIndex = i,
                        RetryCount = retries,
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, ct);
                    return;
                }

                logger?.LogInformation("Broadcast virtual tx {Txid} ({Index}/{Total}) for session {SessionId}",
                    vtx.Txid, i + 1, branch.Count, session.Id);
            }

            // All txs broadcast/confirmed — transition to AwaitingCsvDelay
            logger?.LogInformation("All virtual txs broadcast for session {SessionId}, awaiting CSV delay",
                session.Id);
            await UpdateSession(session with
            {
                State = ExitSessionState.AwaitingCsvDelay,
                NextTxIndex = branch.Count,
                UpdatedAt = DateTimeOffset.UtcNow
            }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            // Transient network errors — log and retry on next poll cycle
            logger?.LogWarning(ex,
                "Transient error progressing broadcasting session {SessionId}, will retry", session.Id);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error progressing broadcasting session {SessionId}", session.Id);
            await FailSession(session, ex.Message, ct);
        }
    }

    /// <summary>
    /// Broadcasts a virtual tx, using 1p1c CPFP package relay when the tx has a P2A anchor
    /// and a fee wallet is available.
    /// </summary>
    private async Task<bool> BroadcastWithCpfpAsync(Transaction tx, CancellationToken ct)
    {
        var anchor = P2ACpfpBuilder.FindP2AAnchor(tx);
        if (anchor is null || feeWallet is null)
        {
            // No P2A anchor or no fee wallet — broadcast directly
            return await blockchain.BroadcastAsync(tx, ct);
        }

        // Estimate fee: first build CPFP child with zero fee to measure its vsize,
        // then rebuild with the correct fee covering both parent and child.
        var feeRate = await blockchain.EstimateFeeRateAsync(6, ct);
        var parentVsize = tx.GetVirtualSize();

        // Initial estimate to select a UTXO large enough
        const int estimatedChildVsize = 155;
        var estimatedTotalFee = feeRate.GetFee(parentVsize + estimatedChildVsize);

        var feeCoin = await feeWallet.SelectFeeUtxoAsync(estimatedTotalFee, ct);
        if (feeCoin is null)
        {
            logger?.LogWarning("No fee UTXO available for CPFP, falling back to direct broadcast");
            return await blockchain.BroadcastAsync(tx, ct);
        }

        var changeScript = await feeWallet.GetChangeScriptAsync(ct);

        // Build the actual child tx to get its real vsize. Signing the fee
        // input is delegated to feeWallet — the SDK never holds a Key.
        var cpfpChild = await P2ACpfpBuilder.BuildCpfpChildAsync(
            tx, feeRate, feeCoin, changeScript, feeWallet, ct);
        var actualChildVsize = cpfpChild.GetVirtualSize();

        // If actual vsize differs significantly, rebuild with corrected fee
        if (Math.Abs(actualChildVsize - estimatedChildVsize) > 10)
        {
            var correctedFeeRate = new FeeRate(
                feeRate.GetFee(parentVsize + actualChildVsize),
                parentVsize + actualChildVsize);
            cpfpChild = await P2ACpfpBuilder.BuildCpfpChildAsync(
                tx, correctedFeeRate, feeCoin, changeScript, feeWallet, ct);
        }

        return await blockchain.BroadcastPackageAsync(tx, cpfpChild, ct);
    }

    private async Task ProgressAwaitingCsvAsync(ExitSession session, CancellationToken ct)
    {
        try
        {
            var vtxoOutpoint = new OutPoint(uint256.Parse(session.VtxoTxid), session.VtxoVout);
            var branch = await virtualTxStorage.GetBranchAsync(vtxoOutpoint, ct);

            if (branch.Count == 0)
            {
                await FailSession(session, "Virtual tx branch not found", ct);
                return;
            }

            // Check that the leaf tx (last in branch) is confirmed
            var leafTx = branch[^1];
            var leafTxid = uint256.Parse(leafTx.Txid);
            var leafStatus = await blockchain.GetTxStatusAsync(leafTxid, ct);

            if (!leafStatus.Confirmed || leafStatus.BlockHeight is null)
            {
                logger?.LogDebug("Leaf tx {Txid} not yet confirmed for session {SessionId}",
                    leafTx.Txid, session.Id);
                return;
            }

            // Get server info for CSV delay
            var serverInfo = await transport.GetServerInfoAsync(ct);
            var csvDelay = serverInfo.UnilateralExit.Value;

            // Check if CSV delay has passed
            var chainTime = await blockchain.GetChainTime(ct);
            var confirmHeight = leafStatus.BlockHeight.Value;

            if (chainTime.Height >= confirmHeight + csvDelay)
            {
                logger?.LogInformation(
                    "CSV delay passed for session {SessionId} (confirm={ConfirmH}, current={CurrentH}, csv={Csv})",
                    session.Id, confirmHeight, chainTime.Height, csvDelay);

                await UpdateSession(session with
                {
                    State = ExitSessionState.Claimable,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, ct);
            }
            else
            {
                var remaining = confirmHeight + csvDelay - chainTime.Height;
                logger?.LogDebug(
                    "CSV delay not yet passed for session {SessionId}, {Remaining} blocks remaining",
                    session.Id, remaining);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error checking CSV delay for session {SessionId}", session.Id);
        }
    }

    private async Task ProgressClaimableAsync(ExitSession session, CancellationToken ct)
    {
        try
        {
            var vtxoOutpoint = new OutPoint(uint256.Parse(session.VtxoTxid), session.VtxoVout);

            // Get VTXO info
            var vtxos = await vtxoStorage.GetVtxos(
                outpoints: [vtxoOutpoint],
                cancellationToken: ct);
            var vtxo = vtxos.FirstOrDefault();
            if (vtxo is null)
            {
                await FailSession(session, "VTXO not found in storage", ct);
                return;
            }

            // Get contract for the VTXO to build the claim witness
            var contracts = await contractStorage.GetContracts(
                scripts: [vtxo.Script],
                cancellationToken: ct);
            var contractEntity = contracts.FirstOrDefault();
            if (contractEntity is null)
            {
                await FailSession(session, "Contract not found for VTXO script", ct);
                return;
            }

            // Parse the contract to get UnilateralPath tapscript
            var serverInfo = await transport.GetServerInfoAsync(ct);
            var contract = ArkContractParser.Parse(contractEntity.Type, contractEntity.AdditionalData, serverInfo.Network);
            if (contract is null)
            {
                await FailSession(session, "Failed to parse contract", ct);
                return;
            }

            // Get the unilateral path tapscript and spend info
            var unilateralTapScript = GetUnilateralPathTapScript(contract);
            if (unilateralTapScript is null)
            {
                await FailSession(session, "Contract does not support unilateral exit", ct);
                return;
            }

            var tapScript = unilateralTapScript.Build();
            var spendInfo = contract.GetTaprootSpendInfo();
            var controlBlock = spendInfo.GetControlBlock(tapScript);

            // Get signer for the wallet
            var signer = await walletProvider.GetSignerAsync(session.WalletId, ct);
            if (signer is null)
            {
                await FailSession(session, "Wallet signer not available", ct);
                return;
            }

            // Get the leaf tx to find the VTXO output
            var branch = await virtualTxStorage.GetBranchAsync(vtxoOutpoint, ct);
            var leafTx = branch.Count > 0 ? branch[^1] : null;
            if (leafTx?.Hex is null)
            {
                await FailSession(session, "Leaf tx hex not available", ct);
                return;
            }

            var parsedLeafTx = ParseVirtualTx(leafTx.Hex, serverInfo.Network, leafTx.Type);

            // Find the VTXO output in the leaf tx
            var vtxoTxOut = parsedLeafTx.Outputs.AsIndexedOutputs()
                .FirstOrDefault(o => o.TxOut.ScriptPubKey.ToHex() == vtxo.Script);
            if (vtxoTxOut is null)
            {
                await FailSession(session, "VTXO output not found in leaf tx", ct);
                return;
            }

            // Build claim transaction
            var claimAddress = BitcoinAddress.Create(session.ClaimAddress, serverInfo.Network);
            var feeRate = await blockchain.EstimateFeeRateAsync(6, ct);

            var claimTx = Helpers.TransactionHelpers.BuildCsvSpendTransaction(
                vtxoOutpoint,
                vtxoTxOut.TxOut,
                claimAddress,
                serverInfo.UnilateralExit,
                tapScript,
                controlBlock,
                feeRate,
                serverInfo.Network);

            // Sign the claim tx
            var precomputedData = claimTx.PrecomputeTransactionData([vtxoTxOut.TxOut]);
            var sighash = claimTx.GetSignatureHashTaproot(
                precomputedData,
                new TaprootExecutionData(0, tapScript.LeafHash)
                {
                    SigHash = TaprootSigHash.Default
                });

            var descriptor = contractEntity.AdditionalData.TryGetValue("user", out var userDesc)
                ? Abstractions.Extensions.KeyExtensions.ParseOutputDescriptor(userDesc, serverInfo.Network)
                : null;

            if (descriptor is null)
            {
                await FailSession(session, "User descriptor not found in contract data", ct);
                return;
            }

            var (_, sig) = await signer.Sign(descriptor, sighash, ct);

            claimTx.Inputs[0].WitScript = new WitScript(
                [sig.ToBytes(), tapScript.Script.ToBytes(), controlBlock.ToBytes()],
                true);

            // Broadcast claim tx
            var success = await blockchain.BroadcastAsync(claimTx, ct);
            if (!success)
            {
                logger?.LogWarning("Failed to broadcast claim tx for session {SessionId}", session.Id);
                return;
            }

            var claimTxid = claimTx.GetHash().ToString();
            logger?.LogInformation("Broadcast claim tx {ClaimTxid} for session {SessionId}",
                claimTxid, session.Id);

            await UpdateSession(session with
            {
                State = ExitSessionState.Claiming,
                ClaimTxid = claimTxid,
                UpdatedAt = DateTimeOffset.UtcNow
            }, ct);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error claiming for session {SessionId}", session.Id);
            await FailSession(session, ex.Message, ct);
        }
    }

    private async Task ProgressClaimingAsync(ExitSession session, CancellationToken ct)
    {
        if (session.ClaimTxid is null)
        {
            await FailSession(session, "Claim txid is null in Claiming state", ct);
            return;
        }

        try
        {
            var claimTxid = uint256.Parse(session.ClaimTxid);
            var status = await blockchain.GetTxStatusAsync(claimTxid, ct);

            if (status.Confirmed)
            {
                logger?.LogInformation(
                    "Claim tx {ClaimTxid} confirmed at height {Height} for session {SessionId}",
                    session.ClaimTxid, status.BlockHeight, session.Id);

                await UpdateSession(session with
                {
                    State = ExitSessionState.Completed,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, ct);
            }
            else if (!status.InMempool)
            {
                // Claim tx disappeared from mempool — may need to rebroadcast
                logger?.LogWarning(
                    "Claim tx {ClaimTxid} not found in mempool or chain for session {SessionId}",
                    session.ClaimTxid, session.Id);

                // Go back to Claimable to retry
                await UpdateSession(session with
                {
                    State = ExitSessionState.Claimable,
                    ClaimTxid = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, ct);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error checking claim tx for session {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// Parse a virtual tx as returned by arkd's <c>GetVirtualTxs</c>
    /// indexer endpoint into a signed, broadcastable transaction.
    /// </summary>
    /// <remarks>
    /// arkd emits the tx as a PSBT-encoded string and the witness layout
    /// depends on which kind of tx we're looking at — mirroring the
    /// approach in arkade-os/ts-sdk's <c>Unroll.Session</c>:
    ///
    /// - <c>Tree</c>: cosigned via the taproot key-path (MuSig2). The
    ///   aggregated Schnorr signature is carried in
    ///   <c>PSBT_IN_TAP_KEY_SIG</c> (<see cref="PSBTInput.TaprootKeySignature"/>).
    ///   The final witness is just <c>[sig]</c>; we synthesize it
    ///   manually because the PSBT doesn't carry <c>witness_utxo</c>
    ///   (which NBitcoin's <c>Finalize()</c> requires).
    /// - <c>Ark</c> / <c>Checkpoint</c> / fallback: try standard
    ///   <c>Finalize() + ExtractTransaction()</c>; if that throws (e.g.
    ///   missing prevouts), fall back to lifting <c>FinalScriptWitness</c>
    ///   straight off each input.
    /// </remarks>
    private static Transaction ParseVirtualTx(string hex, Network network, ChainedTxType type)
    {
        var psbt = PSBT.Parse(hex, network);

        if (type == ChainedTxType.Tree)
        {
            var tx = psbt.GetGlobalTransaction();
            for (var i = 0; i < psbt.Inputs.Count && i < tx.Inputs.Count; i++)
            {
                var sig = psbt.Inputs[i].TaprootKeySignature;
                if (sig is null)
                {
                    // Expected for a preconfirmed VTXO: arkd co-signs the tree only during a
                    // batch round, so the signature is absent until the batch fires. Surface a
                    // typed, transient signal so ProgressBroadcastingAsync can re-fetch and wait
                    // instead of failing the exit. See TreeTxNotSignedException.
                    throw new TreeTxNotSignedException(
                        $"Tree tx {tx.GetHash()} input {i} is missing the MuSig2 " +
                        "taproot key-path signature (PSBT_IN_TAP_KEY_SIG); arkd populates it " +
                        "during the batch round, so the VTXO is likely still preconfirmed.");
                }
                // The `true` flag tells NBitcoin these bytes are stack
                // pushes, not a pre-serialized witness — same idiom as
                // ChainSwapMusigSession / P2ACpfpBuilder elsewhere in
                // the codebase. Without it the constructor tries to
                // deserialize the sig bytes as a witness with a count
                // prefix and throws "No more byte to read".
                tx.Inputs[i].WitScript = new WitScript(new[] { sig.ToBytes() }, true);
            }
            return tx;
        }

        // Ark / Checkpoint / Unspecified: try the standard PSBT finalize
        // path first. If the PSBT lacks witness_utxo it'll throw — fall
        // back to lifting whatever FinalScriptWitness arkd populated.
        try
        {
            psbt.Finalize();
            return psbt.ExtractTransaction();
        }
        catch (PSBTException)
        {
            var tx = psbt.GetGlobalTransaction();
            for (var i = 0; i < psbt.Inputs.Count && i < tx.Inputs.Count; i++)
            {
                var psbtInput = psbt.Inputs[i];
                if (psbtInput.FinalScriptWitness is not null)
                    tx.Inputs[i].WitScript = psbtInput.FinalScriptWitness;
                if (psbtInput.FinalScriptSig is not null)
                    tx.Inputs[i].ScriptSig = psbtInput.FinalScriptSig;
            }
            return tx;
        }
    }

    private static Scripts.UnilateralPathArkTapScript? GetUnilateralPathTapScript(ArkContract contract)
    {
        return contract switch
        {
            ArkPaymentContract pc => (Scripts.UnilateralPathArkTapScript)pc.UnilateralPath(),
            ArkBoardingContract bc => (Scripts.UnilateralPathArkTapScript)bc.UnilateralPath(),
            _ => null
        };
    }

    private async Task UpdateSession(ExitSession session, CancellationToken ct)
    {
        await exitSessionStorage.UpsertAsync(session, ct);
    }

    private async Task FailSession(ExitSession session, string reason, CancellationToken ct)
    {
        logger?.LogError("Exit session {SessionId} failed: {Reason}", session.Id, reason);
        await exitSessionStorage.UpsertAsync(session with
        {
            State = ExitSessionState.Failed,
            FailReason = reason,
            UpdatedAt = DateTimeOffset.UtcNow
        }, ct);
    }
}
