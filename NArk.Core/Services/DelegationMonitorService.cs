using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.Services;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NArk.Core.Helpers;
using NArk.Core.Assets;
using NArk.Core.Models;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Core.Services;

/// <summary>
/// Hosted service that monitors VTXO changes and automatically delegates
/// new VTXOs at delegate contracts to the configured delegator service.
/// </summary>
/// <remarks>
/// A VTXO whose value cannot cover the operator's intent fee and still leave at least the
/// server dust threshold is skipped (the delegation would be rejected with AMOUNT_TOO_LOW);
/// it is re-evaluated on its next storage notification. Stopping the service cancels any
/// delegation still in flight and waits for it to unwind.
/// </remarks>
public class DelegationMonitorService(
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IEnumerable<IDelegationTransformer> transformers,
    IDelegatorProvider delegatorProvider,
    IWalletProvider walletProvider,
    IClientTransport clientTransport,
    IFeeEstimator feeEstimator,
    ILogger<DelegationMonitorService>? logger = null) : IHostedService, IDisposable
{
    private readonly HashSet<OutPoint> _delegatedOutpoints = new();
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private CancellationTokenSource? _shutdownCts;
    private ECPubKey? _delegatePubkey;
    private int _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdownCts = new CancellationTokenSource();
        vtxoStorage.VtxosChanged += OnVtxosChanged;
        logger?.LogInformation("DelegationMonitorService started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged -= OnVtxosChanged;

        // Cancel any delegation already in flight (it may be blocked on a gRPC call to the
        // delegator or the operator) and then drain: acquiring the processing lock guarantees
        // the handler has unwound before the host considers this service stopped.
        // _shutdownCts is null once Dispose has run, which is a legal ordering for a host that
        // disposes the container before (or without) stopping the service.
        if (_shutdownCts is { } cts)
        {
            try
            {
                await cts.CancelAsync();
                await _processingLock.WaitAsync(cancellationToken);
                _processingLock.Release();
            }
            catch (OperationCanceledException)
            {
                logger?.LogWarning("Timed out draining in-flight delegation during shutdown");
            }
            catch (ObjectDisposedException)
            {
                // Raced with Dispose; nothing left to drain.
            }
        }

        logger?.LogInformation("DelegationMonitorService stopped");
    }

    private async void OnVtxosChanged(object? sender, ArkVtxo vtxo)
    {
        // Dispose() may have raced this handler; a disposed CTS throws on .Token.
        CancellationToken cancellationToken;
        try
        {
            cancellationToken = _shutdownCts?.Token ?? CancellationToken.None;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (vtxo.IsSpent())
                return;

            await ProcessVtxoAsync(vtxo, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger?.LogInformation("Delegation of VTXO {Outpoint} cancelled by shutdown",
                $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error processing VTXO {Outpoint} for delegation",
                $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}");
        }
    }

    private async Task ProcessVtxoAsync(ArkVtxo vtxo, CancellationToken cancellationToken)
    {
        await _processingLock.WaitAsync(cancellationToken);
        try
        {
            var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), vtxo.TransactionOutputIndex);

            if (_delegatedOutpoints.Contains(outpoint))
                return;

            var contracts = await contractStorage.GetContracts(scripts: [vtxo.Script], cancellationToken: cancellationToken);
            var contract = contracts.FirstOrDefault();
            if (contract is null)
                return;

            var walletId = contract.WalletIdentifier;
            using var _walletScope = logger?.BeginScope(("WalletId", walletId));
            var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
            var parsed = ArkContractParser.Parse(contract.Type, contract.AdditionalData, serverInfo.Network);
            if (parsed is null)
                return;

            var delegatePubkey = await GetDelegatePubkeyAsync(cancellationToken);

            IDelegationTransformer? matchingTransformer = null;
            foreach (var transformer in transformers)
            {
                if (await transformer.CanDelegate(walletId, parsed, delegatePubkey))
                {
                    matchingTransformer = transformer;
                    break;
                }
            }

            if (matchingTransformer is null)
                return;

            logger?.LogInformation("Delegating VTXO {Outpoint} from wallet {WalletId}", outpoint, walletId);

            var (intentScript, forfeitScript) = matchingTransformer.GetDelegationScriptBuilders(parsed);
            var delegated = await BuildAndSendDelegationAsync(
                walletId, parsed, vtxo, outpoint, intentScript, forfeitScript, serverInfo, cancellationToken);

            if (!delegated)
                return;

            _delegatedOutpoints.Add(outpoint);
            logger?.LogInformation("Successfully delegated VTXO {Outpoint}", outpoint);
        }
        finally
        {
            _processingLock.Release();
        }
    }

    /// <returns>
    /// <c>true</c> when the delegation was sent to the delegator; <c>false</c> when the VTXO
    /// was skipped because it cannot fund the intent fee (see the dust guard below).
    /// </returns>
    private async Task<bool> BuildAndSendDelegationAsync(
        string walletId,
        ArkContract contract,
        ArkVtxo vtxo,
        OutPoint outpoint,
        ScriptBuilder intentScriptBuilder,
        ScriptBuilder forfeitScriptBuilder,
        ArkServerInfo serverInfo,
        CancellationToken cancellationToken)
    {
        // Get signing descriptor from the contract's user key
        var signerDescriptor = contract switch
        {
            ArkDelegateContract dc => dc.User,
            _ => throw new InvalidOperationException($"Unsupported contract type for delegation: {contract.Type}")
        };

        var signer = await walletProvider.GetSignerOrThrowAsync(walletId, cancellationToken);
        var delegatePubkey = await GetDelegatePubkeyAsync(cancellationToken);

        // Build the intent message. Field names must be snake_case (Messages.RegisterIntentMessage's
        // JsonPropertyName values) to match arkd/Fulmine's Go RegisterMessage struct tags — a
        // camelCase mismatch silently unmarshals to zero values rather than erroring. The delegator
        // schedules its registration task at ValidAt (time.Unix(message.ValidAt, 0) in Fulmine's
        // delegator_service.go) via its own scheduler and rejects ValidAt == 0 outright ("invalid
        // valid at"). ts-sdk's DelegateManager always schedules at least 1s in the future (its
        // production default is ~10% before expiry); a zero-delay "now" was observed to make
        // Fulmine's reactive per-join event stream (delegator_service.go's joinDelegateBatch, which
        // opens a fresh GetEventStream only after seeing BatchStartedEvent) consistently lose the
        // race against arkd's TreeTxEvent broadcast, ending in VTXO_BANNED. Mirror ts-sdk's margin.
        var intentMessage = JsonSerializer.Serialize(new Messages.RegisterIntentMessage
        {
            Type = "register",
            OnchainOutputsIndexes = [],
            ValidAt = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds(),
            ExpireAt = 0,
            // Cosigner must be the delegate (who joins future rounds on the owner's behalf),
            // not the owner's own key — the owner is offline, so naming it here just stalls
            // the round waiting for a nonce nobody sends (SIGNING_SESSION_TIMED_OUT).
            CosignersPublicKeys = [Convert.ToHexString(delegatePubkey.ToBytes()).ToLowerInvariant()]
        });

        // Build intent proof PSBT (BIP322-style)
        var intentCoin = new ArkCoin(
            walletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            outpoint, vtxo.TxOut, signerDescriptor, intentScriptBuilder,
            null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);

        // The send-to-self destination is the same script as the original VTXO, minus the
        // operator's intent fee (arkd's INTENT_INSUFFICIENT_FEE rejects a same-amount
        // send-to-self as paying zero fee).
        var destinationAddress = vtxo.TxOut.ScriptPubKey.GetDestinationAddress(serverInfo.Network)
            ?? throw new InvalidOperationException($"Cannot derive destination address for script {vtxo.Script}");
        var destinationOutput = new ArkTxOut(ArkTxOutType.Vtxo, vtxo.TxOut.Value, destinationAddress);
        var fee = await feeEstimator.EstimateFeeAsync([intentCoin], [destinationOutput], cancellationToken);
        var destinationAmount = vtxo.TxOut.Value - Money.Satoshis(fee);

        // A VTXO that cannot cover the intent fee has nothing to delegate: a fee at or above
        // its value builds a zero/negative TxOut that NBitcoin rejects outright, and anything
        // under the operator's dust threshold is rejected by arkd with AMOUNT_TOO_LOW. Skip it
        // instead of sending a doomed delegation — bare-dust VTXOs (typically asset VTXOs)
        // must be consolidated with a larger VTXO before they can be delegated. The outpoint is
        // deliberately not recorded as delegated, so a later fee-estimate drop can still make it
        // eligible on the next notification.
        if (destinationAmount <= Money.Zero || destinationAmount < serverInfo.Dust)
        {
            logger?.LogWarning(
                "Skipping delegation of VTXO {Outpoint}: value {Value} sat minus intent fee {Fee} sat leaves " +
                "{Remaining} sat, below the operator dust threshold of {Dust} sat",
                outpoint, vtxo.TxOut.Value.Satoshi, fee, destinationAmount.Satoshi, serverInfo.Dust.Satoshi);
            return false;
        }

        var intentPsbt = IntentProofHelper.CreateBip322Psbt(intentMessage, serverInfo.Network, intentCoin);

        // CreateBip322Psbt leaves a bare 0-value OP_RETURN placeholder output (the base BIP322
        // proof shape). Replace it with the real send-to-self destination, mirroring
        // IntentGenerationService.CreateIntent's Outputs.RemoveAt(0)/AddRange(outputs).
        var proofGtx = intentPsbt.GetGlobalTransaction();
        proofGtx.Outputs.RemoveAt(0);
        proofGtx.Outputs.Add(new TxOut(destinationAmount, vtxo.TxOut.ScriptPubKey));
        intentPsbt = PSBT.FromTransaction(proofGtx, serverInfo.Network).UpdateFrom(intentPsbt);

        // Build asset packet if the VTXO carries assets — delegation is send-to-self (vout=0)
        if (vtxo.Assets is { Count: > 0 } vtxoAssets)
        {
            var assetInputs = vtxoAssets
                .Select(a => (a.AssetId, vin: (ushort)1, a.Amount))
                .ToList();
            var assetPacketTxOut = AssetPacketBuilder.Build(assetInputs, null, changeVout: 0);
            if (assetPacketTxOut is not null)
            {
                var gtx = intentPsbt.GetGlobalTransaction();
                gtx.Outputs.Add(assetPacketTxOut);
                intentPsbt = PSBT.FromTransaction(gtx, serverInfo.Network).UpdateFrom(intentPsbt);
            }
        }

        intentPsbt = await IntentProofHelper.SignBip322Proof(
            intentPsbt, intentCoin, signer, serverInfo.Network, cancellationToken);

        // Build forfeit tx using the delegate path, signed with SIGHASH_ALL|ANYONECANPAY
        var forfeitCoin = new ArkCoin(
            walletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            outpoint, vtxo.TxOut, signerDescriptor, forfeitScriptBuilder,
            null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);

        var forfeitTx = CreateForfeitTransaction(serverInfo, forfeitCoin);
        var forfeitPrecomputed = forfeitTx.GetGlobalTransaction()
            .PrecomputeTransactionData([forfeitCoin.TxOut]);

        await PsbtHelpers.SignAndFillPsbt(signer, forfeitCoin, forfeitTx, forfeitPrecomputed,
            TaprootSigHash.All | TaprootSigHash.AnyoneCanPay, cancellationToken);

        await delegatorProvider.DelegateAsync(
            intentMessage,
            intentPsbt.ToBase64(),
            [forfeitTx.ToBase64()],
            cancellationToken: cancellationToken);

        return true;
    }

    private static PSBT CreateForfeitTransaction(ArkServerInfo serverInfo, ArkCoin coin)
    {
        // Matches the 2-output shape ArkTransactionBuilder.ConstructForfeitTx produces
        // (payment to the operator's forfeit address + P2A anchor) — the delegator's forfeit
        // validation requires it. Built as a raw transaction rather than through
        // TransactionBuilder.Send()/BuildPSBT(): at delegation time the batch connector
        // doesn't exist yet (the delegator attaches it later when it joins a batch), so the
        // declared forfeit amount (coin.Amount + assumed dust) is intentionally larger than
        // this single input — TransactionBuilder's balance check would reject that.
        var hasLocktime = coin.LockTime is not null && coin.LockTime != LockTime.Zero;
        var vtxoSequence = coin.Sequence
            ?? (hasLocktime ? new Sequence(0xFFFFFFFE) : new Sequence(0xFFFFFFFF));

        var tx = serverInfo.Network.CreateTransaction();
        tx.Version = 3;
        tx.LockTime = coin.LockTime ?? LockTime.Zero;
        tx.Inputs.Add(new TxIn(coin.Outpoint) { Sequence = vtxoSequence });
        tx.Outputs.Add(new TxOut(coin.Amount + serverInfo.Dust, serverInfo.ForfeitAddress));
        tx.Outputs.Add(new TxOut(Money.Zero, Constants.ArkP2A));

        var forfeitTx = PSBT.FromTransaction(tx, serverInfo.Network);
        forfeitTx.Settings.AutomaticUTXOTrimming = false;
        forfeitTx.AddCoins(coin);
        return forfeitTx;
    }

    private async Task<ECPubKey> GetDelegatePubkeyAsync(CancellationToken cancellationToken)
    {
        if (_delegatePubkey is not null)
            return _delegatePubkey;

        var info = await delegatorProvider.GetDelegatorInfoAsync(cancellationToken);
        _delegatePubkey = ECPubKey.Create(Convert.FromHexString(info.Pubkey));
        return _delegatePubkey;
    }

    /// <summary>
    /// Unsubscribes from VTXO notifications and tears down the shutdown token source.
    /// Idempotent: a host that disposes the container and the service scope both end up here.
    /// </summary>
    public void Dispose()
    {
        // Guard rather than null-check the CTS: a second Dispose (Host.Dispose after the DI
        // scope already disposed this service) must not touch the disposed token source.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        vtxoStorage.VtxosChanged -= OnVtxosChanged;

        var cts = Interlocked.Exchange(ref _shutdownCts, null);
        cts?.Cancel();
        cts?.Dispose();
        _processingLock.Dispose();
    }
}
