using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Helpers;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Core.Services;

/// <summary>
/// Reconciles Arkade transactions that the server has registered as pending — i.e. the
/// SDK called <see cref="IClientTransport.SubmitTx"/> (server locked the inputs as
/// in-flight) but the matching <see cref="IClientTransport.FinalizeTx"/> never followed
/// because the process crashed, the user closed the app, or the network dropped.
/// </summary>
/// <remarks>
/// <para>
/// The Arkade server enforces "you must finalize the exact pending tx; you cannot spend
/// those inputs another way", so without this recovery the user's coins are
/// indefinitely stuck. The server exposes a recovery endpoint
/// (<see cref="IClientTransport.GetPendingTxAsync"/>) gated by a BIP-322 proof of
/// ownership — this service authenticates with proofs derived from each wallet's known
/// VTXOs, retrieves any pending transactions the server is holding, signs the checkpoint
/// PSBTs with the wallet's signer, and finalizes them.
/// </para>
/// <para>
/// <b>What gets signed:</b> a pending transaction arrives entirely from the server, so it
/// is authorized against locally reconstructed expectations before anything is signed.
/// Each checkpoint must pay the spent input's full value into the checkpoint contract this
/// wallet would itself have built, and the accompanying final Arkade transaction must
/// spend exactly those checkpoint outputs while still carrying this wallet's own signature
/// over it. A pending transaction failing either check is outside what the wallet ever
/// authorized: it is rejected with
/// <see cref="UnauthorizedPendingArkTransactionException"/> and never signed.
/// </para>
/// <para>
/// Runs once on host startup across every wallet known to
/// <see cref="IWalletStorage"/>; can also be invoked on-demand for a specific wallet via
/// <see cref="FinalizePendingArkTransactionsAsync"/>. Per-transaction failures are
/// logged and skipped so a single bad pending tx never blocks the wallet from booting —
/// the next start-up retries any unfinalized leftovers.
/// </para>
/// <para>
/// <b>Timing note:</b> the Arkade server marks input VTXOs as pending-spent via an async
/// event projection that runs after <c>SubmitTx</c> returns. Calling recovery in the
/// same process as the original <c>SubmitTx</c> may race that projection (the server
/// returns an empty pending list until the projection catches up). The hands-off path
/// (host startup) never races this — by the time the host restarts, the projection has
/// long since run. If you call <see cref="FinalizePendingArkTransactionsAsync"/> in the
/// same process that just crashed mid-Submit, retry briefly until it returns a non-empty
/// list (the projection is typically caught up within a second).
/// </para>
/// </remarks>
public class PendingArkTransactionRecoveryService(
    IClientTransport clientTransport,
    IWalletStorage walletStorage,
    IWalletProvider walletProvider,
    IVtxoStorage vtxoStorage,
    ICoinService coinService,
    ILogger<PendingArkTransactionRecoveryService>? logger = null)
{
    /// <summary>
    /// Server hard-limits proof intents to 20 inputs; matches the batching shape used by
    /// the Go and TypeScript SDKs.
    /// </summary>
    private const int MaxInputsPerProof = 20;

    /// <summary>
    /// Raised when finalizing a single pending Arkade transaction fails. The recovery
    /// loop continues with the next pending tx regardless — subscribers can use this
    /// to surface a wallet-UI banner, ship telemetry, or schedule a retry. Subscribers
    /// must not throw; handler exceptions are caught and logged but never propagate.
    /// </summary>
    public event EventHandler<PendingTxRecoveryFailureEventArgs>? RecoveryFailed;

    /// <summary>
    /// Invoked by <c>ArkHostedLifecycle</c> on host startup. Sweeps every wallet
    /// known to <see cref="IWalletStorage"/> for stranded pending Arkade transactions
    /// and finalizes them. Failures are scoped per wallet so one bad wallet never
    /// blocks the rest.
    /// </summary>
    public async Task RecoverAllWalletsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var wallets = await walletStorage.LoadAllWallets(cancellationToken);
            foreach (var wallet in wallets)
            {
                try
                {
                    await FinalizePendingArkTransactionsAsync(wallet.Id, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogError(ex,
                        "Pending-tx recovery failed for wallet {WalletId}; other wallets continue",
                        wallet.Id);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // host shutting down — nothing to do
        }
        catch (Exception ex)
        {
            // Absorb anything else (e.g. walletStorage.LoadAllWallets throwing on a DB
            // timeout or connection error). This is a best-effort sweep on host startup —
            // a transient storage failure must not block the host from coming up. The
            // next start-up retries.
            logger?.LogError(ex,
                "Pending-tx recovery sweep aborted before iterating wallets; host startup continues");
        }
    }

    /// <summary>
    /// On-demand pending-tx recovery for a single wallet. Call this from app boot if
    /// you want deterministic timing; the BackgroundService startup hook covers the
    /// hands-off case.
    /// </summary>
    /// <remarks>
    /// Pending transactions that fail local authorization (see the type-level
    /// "what gets signed" note) are rejected without being signed, reported on
    /// <see cref="RecoveryFailed"/> with an
    /// <see cref="UnauthorizedPendingArkTransactionException"/>, and left out of the result.
    /// </remarks>
    /// <returns>The arkTxIds that were successfully finalized during this call.</returns>
    public async Task<IReadOnlyList<string>> FinalizePendingArkTransactionsAsync(string walletId,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var network = serverInfo.Network;

        var signer = await walletProvider.GetSignerAsync(walletId, cancellationToken);
        if (signer is null)
        {
            logger?.LogWarning("Pending-tx recovery: wallet {WalletId} has no signer registered, skipping",
                walletId);
            return [];
        }

        // Include spent VTXOs as proof material: the BIP-322 proof only authenticates
        // wallet identity (it signs a message; it never spends the anchor VTXO), and the
        // VTXOs we want to recover are the ones the server is *holding as in-flight* —
        // arkd reports those as spent in the indexer, and VtxoSync propagates that to
        // local storage. Filtering to !IsSpent here would empty the proof set in the
        // exact scenario this service exists to handle.
        var proofVtxos = (await vtxoStorage.GetVtxos(
            walletIds: [walletId],
            includeSpent: true,
            cancellationToken: cancellationToken))
            .Where(v => !v.Swept)
            .ToList();

        if (proofVtxos.Count == 0)
        {
            logger?.LogDebug("Pending-tx recovery: wallet {WalletId} has no VTXOs to prove ownership over, skipping",
                walletId);
            return [];
        }

        var coins = new List<ArkCoin>(proofVtxos.Count);
        foreach (var vtxo in proofVtxos)
        {
            try
            {
                coins.Add(await coinService.GetCoin(vtxo, walletId, cancellationToken));
            }
            catch (Exception ex)
            {
                // VHTLC and similar contracts can refuse to materialise a coin without
                // additional preimage info. Those VTXOs aren't valid proof material —
                // skip them silently and let the resolvable ones carry the proof.
                logger?.LogDebug(ex,
                    "Pending-tx recovery: skipping VTXO {Outpoint} (no resolvable coin)",
                    vtxo.OutPoint);
            }
        }

        if (coins.Count == 0)
        {
            logger?.LogDebug("Pending-tx recovery: wallet {WalletId} has no resolvable coins, skipping",
                walletId);
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var finalized = new List<string>();

        foreach (var batch in Chunk(coins, MaxInputsPerProof))
        {
            // Each batch authenticates over its first coin — that's all the server
            // needs to identify the owning identity. Mirrors ts-sdk and go-sdk.
            var anchor = batch[0];
            var (proof, message) = await CreateProofAsync(anchor, signer, network, cancellationToken);

            Transport.Models.PendingArkTransaction[] pendingTxs;
            try
            {
                pendingTxs = await clientTransport.GetPendingTxAsync(proof, message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex,
                    "Pending-tx recovery: GetPendingTx failed for wallet {WalletId} (proof anchor {Outpoint})",
                    walletId, anchor.Outpoint);
                continue;
            }

            foreach (var pending in pendingTxs)
            {
                if (!seen.Add(pending.ArkTxId)) continue;

                try
                {
                    await FinalizePendingTxAsync(walletId, pending, serverInfo, signer, cancellationToken);
                    finalized.Add(pending.ArkTxId);
                    logger?.LogInformation(
                        "Pending-tx recovery: finalized {ArkTxId} for wallet {WalletId}",
                        pending.ArkTxId, walletId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    OnPendingTxRecoveryFailed(walletId, pending.ArkTxId, ex);
                }
            }
        }

        return finalized;
    }

    private async Task FinalizePendingTxAsync(string walletId, Transport.Models.PendingArkTransaction pending,
        ArkServerInfo serverInfo, IArkadeWalletSigner signer, CancellationToken cancellationToken)
    {
        var network = serverInfo.Network;

        // Everything in a pending tx comes from the server, so the whole set is validated
        // against locally reconstructed expectations BEFORE a single signature is produced —
        // a signature over a fabricated checkpoint cannot be taken back.
        var validated = new List<ValidatedCheckpoint>(pending.SignedCheckpointTxs.Length);

        foreach (var checkpointBase64 in pending.SignedCheckpointTxs)
        {
            PSBT checkpoint;
            try
            {
                checkpoint = PSBT.Parse(checkpointBase64, network);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                    $"checkpoint PSBT could not be parsed ({ex.Message})");
            }

            // Each checkpoint has exactly one input (the original VTXO outpoint) —
            // see arkd's checkpoint construction (one-input-per-spent-VTXO).
            if (checkpoint.Inputs.Count != 1)
            {
                throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                    $"checkpoint PSBT has {checkpoint.Inputs.Count} inputs, expected exactly 1");
            }

            var inputPrevOut = checkpoint.Inputs[0].PrevOut;
            var coin = await ResolveCheckpointInputAsync(walletId, inputPrevOut, cancellationToken);
            var checkpointCoin = VerifyCheckpointOutput(pending.ArkTxId, coin, checkpoint, serverInfo);

            validated.Add(new ValidatedCheckpoint(coin, checkpointCoin, checkpoint));
        }

        if (validated.Count == 0)
        {
            throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                "server returned no checkpoint transactions");
        }

        VerifyFinalArkTxWasSignedByWallet(pending, validated, network);

        var finalCheckpoints = new List<string>(validated.Count);
        foreach (var entry in validated)
        {
            await SignCheckpointAsync(entry.Coin, entry.Checkpoint, signer, cancellationToken);
            finalCheckpoints.Add(entry.Checkpoint.ToBase64());
        }

        await clientTransport.FinalizeTx(pending.ArkTxId, [.. finalCheckpoints], cancellationToken);
    }

    /// <summary>A server checkpoint that has been matched against a locally rebuilt expectation.</summary>
    /// <param name="Coin">The wallet coin the checkpoint spends.</param>
    /// <param name="CheckpointCoin">The checkpoint's own output, as a spendable coin (what the ark tx spends).</param>
    /// <param name="Checkpoint">The server-supplied checkpoint PSBT.</param>
    private sealed record ValidatedCheckpoint(ArkCoin Coin, ArkCoin CheckpointCoin, PSBT Checkpoint);

    /// <summary>
    /// Rebuilds the checkpoint output this wallet would itself have produced for
    /// <paramref name="coin"/> and rejects the server's checkpoint unless it matches exactly.
    /// </summary>
    /// <remarks>
    /// The signature this service is about to produce uses <c>SIGHASH_DEFAULT</c>, which commits
    /// to every output of the checkpoint, and the server holds the other half of the collaborative
    /// 2-of-2 — so a checkpoint is only signed once its outputs are known to be the ones this
    /// wallet asked for. The expectation is reconstructed exactly as
    /// <c>ArkTransactionBuilder.ConstructArkTransaction</c> builds it: the input's full value paid
    /// into a checkpoint contract of (this coin's spending leaf, the server unroll path), plus a
    /// zero-value P2A anchor.
    /// </remarks>
    /// <returns>The checkpoint's own output as an <see cref="ArkCoin"/>, for binding the ark tx.</returns>
    private static ArkCoin VerifyCheckpointOutput(string arkTxId, ArkCoin coin, PSBT checkpoint,
        ArkServerInfo serverInfo)
    {
        if (coin.Contract.Server is null)
        {
            throw new UnauthorizedPendingArkTransactionException(arkTxId,
                $"input {coin.Outpoint} resolves to a contract with no server key, so the expected " +
                "checkpoint output cannot be reconstructed");
        }

        // Mirrors ArkTransactionBuilder.CreateCheckpointContract.
        var checkpointContract = new GenericArkContract(coin.Contract.Server,
            [coin.SpendingScriptBuilder, serverInfo.CheckpointTapScript]);
        var expectedScriptPubKey = checkpointContract.GetScriptPubKey();

        var gtx = checkpoint.GetGlobalTransaction();
        if (gtx.Outputs.Count != 2)
        {
            throw new UnauthorizedPendingArkTransactionException(arkTxId,
                $"checkpoint for input {coin.Outpoint} has {gtx.Outputs.Count} outputs, expected exactly 2 " +
                "(checkpoint output + P2A anchor)");
        }

        var anchorIndex = -1;
        var payloadIndex = -1;
        for (var i = 0; i < gtx.Outputs.Count; i++)
        {
            if (gtx.Outputs[i].ScriptPubKey == Constants.ArkP2A)
                anchorIndex = i;
            else
                payloadIndex = i;
        }

        if (anchorIndex < 0 || payloadIndex < 0)
        {
            throw new UnauthorizedPendingArkTransactionException(arkTxId,
                $"checkpoint for input {coin.Outpoint} must have exactly one P2A anchor output and one " +
                "checkpoint output");
        }

        if (gtx.Outputs[anchorIndex].Value != Money.Zero)
        {
            throw new UnauthorizedPendingArkTransactionException(arkTxId,
                $"checkpoint for input {coin.Outpoint} funds its P2A anchor with " +
                $"{gtx.Outputs[anchorIndex].Value}, expected zero");
        }

        var payload = gtx.Outputs[payloadIndex];
        if (payload.ScriptPubKey != expectedScriptPubKey)
        {
            throw new UnauthorizedPendingArkTransactionException(arkTxId,
                $"checkpoint for input {coin.Outpoint} pays to {payload.ScriptPubKey} instead of this " +
                $"wallet's checkpoint contract {expectedScriptPubKey}");
        }

        if (payload.Value != coin.Amount)
        {
            throw new UnauthorizedPendingArkTransactionException(arkTxId,
                $"checkpoint for input {coin.Outpoint} pays {payload.Value} into the checkpoint contract " +
                $"instead of the input's full {coin.Amount}");
        }

        return new ArkCoin(
            coin.WalletIdentifier,
            checkpointContract,
            coin.Birth,
            coin.ExpiresAt,
            coin.ExpiresAtHeight,
            new OutPoint(gtx, (uint)payloadIndex),
            payload,
            coin.SignerDescriptor,
            coin.SpendingScriptBuilder,
            coin.SpendingConditionWitness,
            coin.LockTime,
            coin.Sequence,
            coin.Swept,
            coin.Unrolled);
    }

    /// <summary>
    /// Binds the checkpoints to the Arkade transaction the wallet actually authorised: the final
    /// ark tx must spend exactly the validated checkpoint outputs, and every input must still
    /// carry this wallet's own signature over it.
    /// </summary>
    /// <remarks>
    /// A checkpoint output is a 2-of-2 with the server plus a server-only unroll path that opens
    /// after a relative timeout, so a correctly shaped checkpoint on its own does not say where
    /// the funds end up. The wallet's signature on the ark tx is what pins the destination — it
    /// commits (SIGHASH_DEFAULT) to the ark tx's outputs and to all of its prevouts, and only the
    /// wallet's key can produce it. Verifying it here means recovery only ever completes a spend
    /// this wallet built and signed itself.
    /// </remarks>
    private static void VerifyFinalArkTxWasSignedByWallet(Transport.Models.PendingArkTransaction pending,
        IReadOnlyList<ValidatedCheckpoint> validated, Network network)
    {
        PSBT arkTx;
        try
        {
            arkTx = PSBT.Parse(pending.FinalArkTx, network);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                $"final Arkade transaction could not be parsed ({ex.Message})");
        }

        var gtx = arkTx.GetGlobalTransaction();
        if (!string.Equals(gtx.GetHash().ToString(), pending.ArkTxId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                $"final Arkade transaction hashes to {gtx.GetHash()}, which is not the advertised id");
        }

        if (gtx.Inputs.Count != validated.Count)
        {
            throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                $"final Arkade transaction spends {gtx.Inputs.Count} inputs but {validated.Count} " +
                "checkpoint(s) were returned");
        }

        var byOutpoint = new Dictionary<OutPoint, ValidatedCheckpoint>();
        foreach (var entry in validated)
        {
            if (!byOutpoint.TryAdd(entry.CheckpointCoin.Outpoint, entry))
            {
                throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                    $"two checkpoints produce the same output {entry.CheckpointCoin.Outpoint}");
            }
        }

        var ordered = new ValidatedCheckpoint[gtx.Inputs.Count];
        var prevouts = new TxOut[gtx.Inputs.Count];
        for (var i = 0; i < gtx.Inputs.Count; i++)
        {
            if (!byOutpoint.TryGetValue(gtx.Inputs[i].PrevOut, out var match))
            {
                throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                    $"final Arkade transaction spends {gtx.Inputs[i].PrevOut}, which is not one of the " +
                    "checkpoint outputs");
            }

            ordered[i] = match;
            prevouts[i] = match.CheckpointCoin.TxOut;
        }

        var precomputed = gtx.PrecomputeTransactionData(prevouts);
        for (var i = 0; i < ordered.Length; i++)
        {
            var checkpointCoin = ordered[i].CheckpointCoin;

            // Covenant leaves (e.g. an emulator-cosigned HTLC claim) name no wallet key: the
            // wallet never signs the ark tx for those inputs, so there is nothing to verify.
            if (checkpointCoin.SignerDescriptor is null)
                continue;

            var walletKey = checkpointCoin.SignerDescriptor.ToXOnlyPubKey();
            var leafHash = checkpointCoin.SpendingScript.LeafHash;

            if (!arkTx.Inputs[i].TryGetTaprootScriptSpendSignature(walletKey, leafHash, out var signatureBytes))
            {
                throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                    $"final Arkade transaction carries no wallet signature for checkpoint input " +
                    $"{checkpointCoin.Outpoint}");
            }

            // 64 bytes == SIGHASH_DEFAULT. A 65-byte signature carries an explicit sighash flag,
            // which would mean the wallet's signature does not commit to the ark tx's outputs.
            if (signatureBytes.Length != 64 || !SecpSchnorrSignature.TryCreate(signatureBytes, out var signature)
                || signature is null)
            {
                throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                    $"final Arkade transaction carries a malformed or non-default-sighash wallet signature " +
                    $"for checkpoint input {checkpointCoin.Outpoint}");
            }

            var sigHash = gtx.GetSignatureHashTaproot(precomputed,
                new TaprootExecutionData(i, leafHash) { SigHash = TaprootSigHash.Default });

            if (!walletKey.SigVerifyBIP340(signature, sigHash.ToBytes()))
            {
                throw new UnauthorizedPendingArkTransactionException(pending.ArkTxId,
                    $"this wallet's signature on the final Arkade transaction does not verify for " +
                    $"checkpoint input {checkpointCoin.Outpoint}; it is not the transaction the wallet signed");
            }
        }
    }

    /// <summary>
    /// Signs a checkpoint PSBT input in-place with the wallet's signer. Virtual so
    /// unit tests can stub signing without staging a real key + tap-leaf path.
    /// </summary>
    protected virtual async Task SignCheckpointAsync(ArkCoin coin, PSBT checkpoint,
        IArkadeWalletSigner signer, CancellationToken cancellationToken)
    {
        coin.FillPsbtInput(checkpoint);
        var precomputed = checkpoint.GetGlobalTransaction()
            .PrecomputeTransactionData([coin.TxOut]);
        await PsbtHelpers.SignAndFillPsbt(signer, coin, checkpoint, precomputed,
            cancellationToken: cancellationToken);
    }

    private async Task<ArkCoin> ResolveCheckpointInputAsync(string walletId, OutPoint outpoint,
        CancellationToken cancellationToken)
    {
        var hits = await vtxoStorage.GetVtxos(
            walletIds: [walletId],
            outpoints: [outpoint],
            includeSpent: true,
            cancellationToken: cancellationToken);

        var vtxo = hits.FirstOrDefault()
                   ?? throw new InvalidOperationException(
                       $"Pending-tx recovery: no local VTXO matches checkpoint input {outpoint}; " +
                       "run wallet sync (RestoreWallet for HD wallets) before retrying.");

        return await coinService.GetCoin(vtxo, walletId, cancellationToken);
    }

    /// <summary>
    /// Logs the failure and raises <see cref="RecoveryFailed"/>. The loop continues
    /// with the next pending tx regardless, so a single bad tx never blocks recovery
    /// for the rest of the batch / suite.
    /// </summary>
    private void OnPendingTxRecoveryFailed(string walletId, string arkTxId, Exception ex)
    {
        logger?.LogWarning(ex,
            "Pending-tx recovery: finalize failed for {ArkTxId} (wallet {WalletId}); will retry on next service start",
            arkTxId, walletId);

        var handler = RecoveryFailed;
        if (handler is null) return;

        try
        {
            handler.Invoke(this, new PendingTxRecoveryFailureEventArgs(walletId, arkTxId, ex));
        }
        catch (Exception subscriberEx)
        {
            // Subscribers throwing must not break the recovery loop.
            logger?.LogWarning(subscriberEx,
                "Pending-tx recovery: RecoveryFailed handler threw for {ArkTxId} (wallet {WalletId})",
                arkTxId, walletId);
        }
    }

    /// <summary>
    /// Creates the BIP-322-style proof + message that authenticates the GetPendingTx
    /// call. Virtual so unit tests can substitute a canned value without forcing a
    /// real signer.
    /// </summary>
    protected virtual Task<(string Proof, string Message)> CreateProofAsync(
        ArkCoin anchor, IArkadeWalletSigner signer, Network network,
        CancellationToken cancellationToken)
        => IntentProofHelper.CreateGetPendingTxOwnershipProofAsync(anchor, signer, network, cancellationToken);

    private static IEnumerable<List<ArkCoin>> Chunk(IReadOnlyList<ArkCoin> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }
}
