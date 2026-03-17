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
    IOnchainBroadcaster broadcaster,
    IWalletProvider walletProvider,
    IChainTimeProvider chainTimeProvider,
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

            var missingHex = branch.Any(tx => tx.Hex is null);
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
                if (vtx.Hex is null)
                {
                    await FailSession(session, $"Missing hex for virtual tx {vtx.Txid}", ct);
                    return;
                }

                var txid = uint256.Parse(vtx.Txid);

                // Check if already confirmed
                var status = await broadcaster.GetTxStatusAsync(txid, ct);
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

                // Not seen — broadcast
                var tx = Transaction.Parse(vtx.Hex, network);
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
            return await broadcaster.BroadcastAsync(tx, ct);
        }

        // Estimate fee: first build CPFP child with zero fee to measure its vsize,
        // then rebuild with the correct fee covering both parent and child.
        var feeRate = await broadcaster.EstimateFeeRateAsync(6, ct);
        var parentVsize = tx.GetVirtualSize();

        // Initial estimate to select a UTXO large enough
        const int estimatedChildVsize = 155;
        var estimatedTotalFee = feeRate.GetFee(parentVsize + estimatedChildVsize);

        var feeCoin = await feeWallet.SelectFeeUtxoAsync(estimatedTotalFee, ct);
        if (feeCoin is null)
        {
            logger?.LogWarning("No fee UTXO available for CPFP, falling back to direct broadcast");
            return await broadcaster.BroadcastAsync(tx, ct);
        }

        var changeScript = await feeWallet.GetChangeScriptAsync(ct);

        // Build the actual child tx to get its real vsize
        var cpfpChild = P2ACpfpBuilder.BuildCpfpChild(
            tx, feeRate, feeCoin.Outpoint, feeCoin.TxOut, changeScript, feeCoin.SigningKey);
        var actualChildVsize = cpfpChild.GetVirtualSize();

        // If actual vsize differs significantly, rebuild with corrected fee
        if (Math.Abs(actualChildVsize - estimatedChildVsize) > 10)
        {
            var correctedFeeRate = new FeeRate(
                feeRate.GetFee(parentVsize + actualChildVsize),
                parentVsize + actualChildVsize);
            cpfpChild = P2ACpfpBuilder.BuildCpfpChild(
                tx, correctedFeeRate, feeCoin.Outpoint, feeCoin.TxOut, changeScript, feeCoin.SigningKey);
        }

        return await broadcaster.BroadcastPackageAsync(tx, cpfpChild, ct);
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
            var leafStatus = await broadcaster.GetTxStatusAsync(leafTxid, ct);

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
            var chainTime = await chainTimeProvider.GetChainTime(ct);
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

            var parsedLeafTx = Transaction.Parse(leafTx.Hex, serverInfo.Network);

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
            var feeRate = await broadcaster.EstimateFeeRateAsync(6, ct);

            var claimTx = BuildClaimTransaction(
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
            var success = await broadcaster.BroadcastAsync(claimTx, ct);
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
            var status = await broadcaster.GetTxStatusAsync(claimTxid, ct);

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

    private static Transaction BuildClaimTransaction(
        OutPoint vtxoOutpoint,
        TxOut vtxoTxOut,
        BitcoinAddress claimAddress,
        Sequence csvDelay,
        TapScript tapScript,
        ControlBlock controlBlock,
        FeeRate feeRate,
        Network network)
    {
        var claimTx = Transaction.Create(network);
        claimTx.Version = 2;

        // Input: VTXO output with CSV sequence
        var input = claimTx.Inputs.Add(vtxoOutpoint);
        input.Sequence = csvDelay;

        // Estimate size for fee calculation
        // P2TR script-path spend: ~64 sig + script + control block ≈ 150-200 wu
        var witnessSize = 64 + tapScript.Script.Length + controlBlock.ToBytes().Length + 10;
        var txBaseSize = 10 + 41 + 43; // version + input + output overhead
        var vsize = txBaseSize + (witnessSize + 3) / 4;
        var fee = feeRate.GetFee(vsize);

        var claimAmount = vtxoTxOut.Value - fee;
        if (claimAmount <= Money.Zero)
            throw new InvalidOperationException(
                $"VTXO amount ({vtxoTxOut.Value}) is too small to cover fees ({fee})");

        claimTx.Outputs.Add(new TxOut(claimAmount, claimAddress));

        return claimTx;
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
