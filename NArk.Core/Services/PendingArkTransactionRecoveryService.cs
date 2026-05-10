using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Helpers;
using NArk.Core.Transport;
using NBitcoin;

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
/// Runs once on host startup across every wallet known to
/// <see cref="IWalletStorage"/>; can also be invoked on-demand for a specific wallet via
/// <see cref="FinalizePendingArkTransactionsAsync"/>. Per-transaction failures are
/// logged and skipped so a single bad pending tx never blocks the wallet from booting —
/// the next start-up retries any unfinalized leftovers.
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
    }

    /// <summary>
    /// On-demand pending-tx recovery for a single wallet. Call this from app boot if
    /// you want deterministic timing; the BackgroundService startup hook covers the
    /// hands-off case.
    /// </summary>
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

        var spendableVtxos = (await vtxoStorage.GetVtxos(
            walletIds: [walletId],
            includeSpent: false,
            cancellationToken: cancellationToken))
            .Where(v => !v.Swept)
            .ToList();

        if (spendableVtxos.Count == 0)
        {
            logger?.LogDebug("Pending-tx recovery: wallet {WalletId} has no spendable VTXOs to prove ownership over, skipping",
                walletId);
            return [];
        }

        var coins = new List<ArkCoin>(spendableVtxos.Count);
        foreach (var vtxo in spendableVtxos)
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
                    await FinalizePendingTxAsync(walletId, pending, network, signer, cancellationToken);
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
        Network network, IArkadeWalletSigner signer, CancellationToken cancellationToken)
    {
        var finalCheckpoints = new List<string>(pending.SignedCheckpointTxs.Length);

        foreach (var checkpointBase64 in pending.SignedCheckpointTxs)
        {
            var checkpoint = PSBT.Parse(checkpointBase64, network);

            // Each checkpoint has exactly one input (the original VTXO outpoint).
            // Resolve back to a coin via local storage so we can fill the spending
            // witness and sign with the wallet's signer.
            var inputPrevOut = checkpoint.Inputs.Single().PrevOut;
            var coin = await ResolveCheckpointInputAsync(walletId, inputPrevOut, cancellationToken);

            await SignCheckpointAsync(coin, checkpoint, signer, cancellationToken);

            finalCheckpoints.Add(checkpoint.ToBase64());
        }

        await clientTransport.FinalizeTx(pending.ArkTxId, [.. finalCheckpoints], cancellationToken);
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
