using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
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
using NArk.Core.Helpers;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Restore;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NArk.Swaps.Utils;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using OutputDescriptorHelpers = NArk.Abstractions.Extensions.OutputDescriptorHelpers;

namespace NArk.Swaps.Boltz;

/// <summary>
/// Boltz-specific swap provider implementing ISwapProvider.
/// Manages all Boltz protocol interactions: swap creation, status monitoring via
/// WebSocket/polling, cooperative claiming (MuSig2), and cooperative refunds.
/// </summary>
public class BoltzSwapProvider : ISwapProvider
{
    public const string Id = "boltz";

    private readonly BoltzSwapService _boltzService;
    private readonly ChainSwapMusigSession _chainSwapMusig;
    private readonly BoltzClient _boltzClient;
    private readonly BoltzLimitsValidator _limitsValidator;
    private readonly IClientTransport _clientTransport;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IWalletProvider _walletProvider;
    private readonly ISwapStorage _swapsStorage;
    private readonly IContractService _contractService;
    private readonly IContractStorage _contractStorage;
    private readonly ISafetyService _safetyService;
    private readonly SpendingService _spendingService;
    private readonly IChainTimeProvider _chainTimeProvider;
    private readonly TransactionHelpers.ArkTransactionBuilder _transactionBuilder;
    private readonly ILogger<BoltzSwapProvider>? _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<string> _triggerChannel = Channel.CreateUnbounded<string>();

    private HashSet<string> _swapsIdToWatch = [];
    private readonly ConcurrentDictionary<string, string> _scriptToSwapId = [];

    private Task? _cacheTask;
    private Task? _routinePollTask;

    private Task? _lastStreamTask;
    private CancellationTokenSource _restartCts = new();
    private Network? _network;
    private ECXOnlyPubKey? _serverKey;

    public BoltzSwapProvider(
        BoltzClient boltzClient,
        BoltzLimitsValidator limitsValidator,
        IClientTransport clientTransport,
        IVtxoStorage vtxoStorage,
        IWalletProvider walletProvider,
        ISwapStorage swapsStorage,
        IContractService contractService,
        IContractStorage contractStorage,
        ISafetyService safetyService,
        SpendingService spendingService,
        IIntentStorage intentStorage,
        IChainTimeProvider chainTimeProvider,
        ILogger<BoltzSwapProvider>? logger = null)
    {
        _boltzClient = boltzClient;
        _limitsValidator = limitsValidator;
        _clientTransport = clientTransport;
        _vtxoStorage = vtxoStorage;
        _walletProvider = walletProvider;
        _swapsStorage = swapsStorage;
        _contractService = contractService;
        _contractStorage = contractStorage;
        _safetyService = safetyService;
        _spendingService = spendingService;
        _chainTimeProvider = chainTimeProvider;
        _logger = logger;
        _boltzService = new BoltzSwapService(boltzClient, clientTransport);
        _chainSwapMusig = new ChainSwapMusigSession(boltzClient);
        _transactionBuilder = new TransactionHelpers.ArkTransactionBuilder(
            clientTransport, safetyService, walletProvider, intentStorage);
    }

    public string ProviderId => Id;
    public string DisplayName => "Boltz";

    public bool SupportsRoute(SwapRoute route)
    {
        // Boltz supports:
        // Ark <-> Lightning (submarine / reverse submarine)
        // Ark <-> BTC on-chain (chain swaps)
        return route switch
        {
            { Source.Network: SwapNetwork.Ark, Destination.Network: SwapNetwork.Lightning } => true,
            { Source.Network: SwapNetwork.Lightning, Destination.Network: SwapNetwork.Ark } => true,
            { Source.Network: SwapNetwork.Ark, Destination.Network: SwapNetwork.BitcoinOnchain } => true,
            { Source.Network: SwapNetwork.BitcoinOnchain, Destination.Network: SwapNetwork.Ark } => true,
            _ => false
        };
    }

    public Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct)
    {
        IReadOnlyCollection<SwapRoute> routes = new[]
        {
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning),   // Submarine: Ark -> LN
            new SwapRoute(SwapAsset.BtcLightning, SwapAsset.ArkBtc),   // Reverse: LN -> Ark
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain),     // Chain: Ark -> BTC
            new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc),     // Chain: BTC -> Ark
        };
        return Task.FromResult(routes);
    }

    public async Task StartAsync(string walletId, CancellationToken ct)
    {
        _logger?.LogInformation("Starting Boltz swap provider");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);

        var serverInfo = await _clientTransport.GetServerInfoAsync(ct);
        _serverKey = OutputDescriptorHelpers.Extract(serverInfo.SignerKey).XOnlyPubKey;
        _network = serverInfo.Network;
        _routinePollTask = RoutinePoll(TimeSpan.FromMinutes(1), multiToken.Token);
        _cacheTask = DoUpdateStorage(multiToken.Token);
    }

    public Task StopAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public async Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct)
    {
        var isReverse = route.Source.Network == SwapNetwork.Lightning;
        var isChain = route.Source.Network == SwapNetwork.BitcoinOnchain ||
                      route.Destination.Network == SwapNetwork.BitcoinOnchain;

        BoltzLimits? limits;
        if (isChain)
        {
            var isBtcToArk = route.Source.Network == SwapNetwork.BitcoinOnchain;
            limits = await _limitsValidator.GetChainLimitsAsync(isBtcToArk, ct);
        }
        else
        {
            limits = await _limitsValidator.GetLimitsAsync(isReverse, ct);
        }

        if (limits == null)
            throw new InvalidOperationException($"Unable to fetch Boltz limits for route {route}");

        return new SwapLimits
        {
            Route = route,
            MinAmount = limits.MinAmount,
            MaxAmount = limits.MaxAmount,
            FeePercentage = limits.FeePercentage,
            MinerFee = limits.MinerFee
        };
    }

    public async Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct)
    {
        var limits = await GetLimitsAsync(route, ct);
        var fee = (long)(amount * limits.FeePercentage) + limits.MinerFee;
        return new SwapQuote
        {
            Route = route,
            SourceAmount = amount,
            DestinationAmount = amount - fee,
            TotalFees = fee,
            ExchangeRate = 1m // BTC-to-BTC, same asset
        };
    }

    public event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;

    // ─── Monitoring ────────────────────────────────────────────────

    /// <summary>
    /// Called by the router when a VTXO changes on a script associated with a Boltz swap.
    /// </summary>
    internal void NotifyVtxoChanged(ArkVtxo vtxo)
    {
        if (_network is null || _serverKey is null) return;

        try
        {
            if (_scriptToSwapId.TryGetValue(vtxo.Script, out var id))
            {
                _triggerChannel.Writer.TryWrite($"id:{id}");
            }
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// Called by the router when a swap record changes in storage.
    /// </summary>
    internal void NotifySwapChanged(ArkSwap swap)
    {
        _triggerChannel.Writer.TryWrite($"id:{swap.SwapId}");
    }

    private async Task RoutinePoll(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _triggerChannel.Writer.TryWrite("");
            await Task.Delay(interval, cancellationToken);
        }
    }

    private async Task DoUpdateStorage(CancellationToken cancellationToken)
    {
        await foreach (var eventDetails in _triggerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                if (eventDetails.StartsWith("id:"))
                {
                    var swapId = eventDetails[3..];

                    // If we already monitor this swap, no need to restart websocket
                    if (_swapsIdToWatch.Contains(swapId))
                    {
                        _logger?.LogDebug("Swap {SwapId} update triggered (already monitored), polling state", swapId);
                        await PollSwapState([swapId], cancellationToken);
                    }
                    else
                    {
                        _logger?.LogInformation("New swap {SwapId} detected, subscribing to websocket updates", swapId);
                        await PollSwapState([swapId], cancellationToken);

                        HashSet<string> newSwapIdSet = [.. _swapsIdToWatch, swapId];
                        _swapsIdToWatch = newSwapIdSet;

                        var newRestartCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                        _lastStreamTask = DoStatusCheck(newSwapIdSet, newRestartCts.Token);
                        await _restartCts.CancelAsync();
                        _restartCts = newRestartCts;
                    }
                }
                else
                {
                    var activeSwaps =
                        await _swapsStorage.GetSwaps(active: true, cancellationToken: cancellationToken);
                    var newSwapIdSet =
                        activeSwaps.Select(s => s.SwapId).ToHashSet();

                    if (_swapsIdToWatch.SetEquals(newSwapIdSet))
                    {
                        // Set unchanged, but still poll as a failsafe (websocket may have dropped)
                        if (newSwapIdSet.Count > 0)
                        {
                            _logger?.LogDebug("Routine poll: {Count} active swap(s), polling states as failsafe", newSwapIdSet.Count);
                            await PollSwapState(newSwapIdSet, cancellationToken);
                        }
                        continue;
                    }

                    _logger?.LogInformation("Active swap set changed: {OldCount} -> {NewCount} swap(s), restarting websocket",
                        _swapsIdToWatch.Count, newSwapIdSet.Count);
                    await PollSwapState(newSwapIdSet.Except(_swapsIdToWatch), cancellationToken);

                    _swapsIdToWatch = newSwapIdSet;

                    var newRestartCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                    _lastStreamTask = DoStatusCheck(newSwapIdSet, newRestartCts.Token);
                    await _restartCts.CancelAsync();
                    _restartCts = newRestartCts;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Error processing swap update trigger: {Details}", eventDetails);
            }
        }
    }

    internal async Task PollSwapState(IEnumerable<string> idsToPoll, CancellationToken cancellationToken)
    {
        foreach (var idToPoll in idsToPoll)
        {
            try
            {
                var swapStatus = await _boltzClient.GetSwapStatusAsync(idToPoll, _shutdownCts.Token);
                if (swapStatus?.Status is null)
                {
                    _logger?.LogDebug("Swap {SwapId}: Boltz returned null status", idToPoll);
                    continue;
                }

                await using var @lock = await _safetyService.LockKeyAsync($"swap::{idToPoll}", cancellationToken);
                var swaps = await _swapsStorage.GetSwaps(swapIds: [idToPoll], cancellationToken: cancellationToken);
                var swap = swaps.FirstOrDefault();
                if (swap == null)
                {
                    _logger?.LogWarning("Swap {SwapId}: not found in storage", idToPoll);
                    continue;
                }
                _scriptToSwapId[swap.ContractScript] = swap.SwapId;

                // Terminal states: nothing to do
                if (swap.Status is ArkSwapStatus.Refunded or ArkSwapStatus.Settled) continue;

                // If not refunded and status is refundable, start a coop refund
                if (swap.SwapType is ArkSwapType.Submarine && swap.Status is not ArkSwapStatus.Refunded &&
                    IsRefundableStatus(swapStatus.Status))
                {
                    _logger?.LogInformation("Swap {SwapId}: Boltz status '{BoltzStatus}' is refundable, initiating cooperative refund",
                        idToPoll, swapStatus.Status);
                    var newSwap =
                        swap with { Status = ArkSwapStatus.Failed, UpdatedAt = DateTimeOffset.Now };
                    await RequestRefundCooperatively(newSwap, cancellationToken);
                    // Don't map status to Failed below — if refund succeeded, status is already
                    // Refunded in storage; if it returned early (e.g. VTXOs not yet available
                    // due to batch round race), keep the swap Pending so routine polls retry.
                    continue;
                }

                // For ARK→BTC chain swaps: try to claim BTC when server has locked
                if (swap.SwapType is ArkSwapType.ChainArkToBtc &&
                    IsChainSwapClaimableStatus(swapStatus.Status))
                {
                    await TryClaimBtcForChainSwap(swap, cancellationToken);
                }

                // For BTC→ARK chain swaps: provide cooperative cross-signature so Boltz
                // can claim our BTC lockup via key-path (more efficient than script-path).
                // This is non-critical — Boltz can eventually claim via script-path with preimage.
                if (swap.SwapType is ArkSwapType.ChainBtcToArk &&
                    swapStatus.Status is "transaction.claim.pending")
                {
                    await TrySignBoltzBtcClaim(swap, cancellationToken);
                }

                // Re-read swap — claim handlers may have updated status to terminal
                var updatedSwaps = await _swapsStorage.GetSwaps(swapIds: [idToPoll], cancellationToken: cancellationToken);
                swap = updatedSwaps.FirstOrDefault() ?? swap;
                if (swap.Status is ArkSwapStatus.Settled or ArkSwapStatus.Refunded) continue;

                var newStatus = MapBoltzStatus(swapStatus.Status);

                if (swap.Status == newStatus) continue;

                _logger?.LogInformation("Swap {SwapId}: status changed {OldStatus} -> {NewStatus} (Boltz: '{BoltzStatus}')",
                    idToPoll, swap.Status, newStatus, swapStatus.Status);

                var swapWithNewStatus =
                    swap with { Status = newStatus, UpdatedAt = DateTimeOffset.Now };

                await _swapsStorage.SaveSwap(swap.WalletId,
                    swapWithNewStatus, cancellationToken: cancellationToken);

                if (swapWithNewStatus.Status is ArkSwapStatus.Settled or ArkSwapStatus.Refunded)
                {
                    _logger?.LogInformation("Swap {SwapId}: terminal state {Status}, removing from watch list",
                        idToPoll, swapWithNewStatus.Status);
                    _scriptToSwapId.Remove(swapWithNewStatus.ContractScript, out _);
                    _swapsIdToWatch.Remove(swapWithNewStatus.SwapId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Swap {SwapId}: error polling state from Boltz", idToPoll);
            }
        }
    }

    // ─── Cooperative Refund ────────────────────────────────────────

    internal async Task RequestRefundCooperatively(ArkSwap swap, CancellationToken cancellationToken = default)
    {
        if (swap.SwapType != ArkSwapType.Submarine)
        {
            throw new InvalidOperationException("Only submarine swaps can be refunded");
        }

        if (swap.Status == ArkSwapStatus.Refunded)
        {
            return;
        }

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
            _logger?.LogWarning("Swap {SwapId}: no VTXOs found for cooperative refund", swap.SwapId);
            return;
        }

        // Use the first VTXO (should only be one for a swap)
        var vtxo = vtxos.Single();

        var timeHeight = await _chainTimeProvider.GetChainTime(cancellationToken);
        if (!vtxo.CanSpendOffchain(timeHeight))
            return;

        // Get the user's wallet address for refund destination
        var refundAddress =
            await _contractService.DeriveContract(swap.WalletId, NextContractPurpose.SendToSelf,
                ContractActivityState.AwaitingFundsBeforeDeactivate,
                metadata: new Dictionary<string, string> { ["Source"] = $"swap:{swap.SwapId}" },
                cancellationToken: cancellationToken);
        if (refundAddress == null)
        {
            throw new InvalidOperationException("Failed to get refund address");
        }

        try
        {
            var arkCoin = contract.ToCoopRefundCoin(swap.WalletId, vtxo);

            var (arkTx, checkpoints) =
                await _transactionBuilder.ConstructArkTransaction([arkCoin],
                    [new ArkTxOut(ArkTxOutType.Vtxo, arkCoin.Amount, refundAddress.GetArkAddress())],
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
                swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.Now };

            await _swapsStorage.SaveSwap(newSwap.WalletId, newSwap, cancellationToken);
            _logger?.LogInformation("Swap {SwapId}: cooperative refund completed successfully", swap.SwapId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Swap {SwapId}: cooperative refund failed, deactivating refund contract", swap.SwapId);
            await _contractStorage.SaveContract(
                refundAddress.ToEntity(swap.WalletId, activityState: ContractActivityState.Inactive),
                cancellationToken);
            throw;
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

    // ─── Status Mapping ────────────────────────────────────────────

    internal static ArkSwapStatus MapBoltzStatus(string status)
    {
        return status switch
        {
            "swap.created" or "invoice.set" => ArkSwapStatus.Pending,
            "invoice.failedToPay" or "invoice.expired" or "swap.expired" or "transaction.failed"
                or "transaction.refunded" =>
                ArkSwapStatus.Failed,
            "transaction.mempool" or "transaction.confirmed" => ArkSwapStatus.Pending,
            "invoice.settled" or "transaction.claimed" => ArkSwapStatus.Settled,
            // Chain swap specific statuses
            "transaction.server.mempool" or "transaction.server.confirmed"
                or "transaction.claim.pending" => ArkSwapStatus.Pending,
            "transaction.lockupFailed" => ArkSwapStatus.Failed,
            _ => ArkSwapStatus.Unknown
        };
    }

    internal static bool IsRefundableStatus(string status)
    {
        return status switch
        {
            "invoice.failedToPay" => true,
            "invoice.expired" => true,
            "swap.expired" => true,
            "transaction.lockupFailed" => true,
            _ => false
        };
    }

    private static bool IsChainSwapClaimableStatus(string status)
    {
        return status is "transaction.server.mempool" or "transaction.server.confirmed";
    }

    // ─── WebSocket ─────────────────────────────────────────────────

    private async Task DoStatusCheck(HashSet<string> swapsIds, CancellationToken cancellationToken)
    {
        if (swapsIds.Count == 0) return;

        var wsUri = _boltzClient.DeriveWebSocketUri();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger?.LogInformation("Connecting to Boltz websocket at {Uri} for {Count} swap(s)", wsUri, swapsIds.Count);
                await using var websocketClient = new BoltzWebsocketClient(wsUri);
                websocketClient.OnAnyEventReceived += OnSwapEventReceived;
                try
                {
                    await websocketClient.ConnectAsync(cancellationToken);
                    await websocketClient.SubscribeAsync(swapsIds.ToArray(), cancellationToken);
                    _logger?.LogInformation("Boltz websocket connected, subscribed to: {SwapIds}", string.Join(", ", swapsIds));
                    await websocketClient.WaitUntilDisconnected(cancellationToken);
                    _logger?.LogWarning("Boltz websocket disconnected");
                }
                finally
                {
                    websocketClient.OnAnyEventReceived -= OnSwapEventReceived;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Boltz websocket error, reconnecting in 5s");
            }

            if (!cancellationToken.IsCancellationRequested)
                await Task.Delay(5000, cancellationToken);
        }
    }

    private Task OnSwapEventReceived(WebSocketResponse? response)
    {
        try
        {
            if (response is null)
                return Task.CompletedTask;

            if (response.Event == "update" && response is { Channel: "swap.update", Args.Count: > 0 })
            {
                var swapUpdate = response.Args[0];
                if (swapUpdate != null)
                {
                    var id = swapUpdate["id"]!.GetValue<string>();
                    var status = swapUpdate["status"]?.GetValue<string>();
                    _logger?.LogDebug("Websocket event: swap {SwapId} status '{Status}'", id, status);
                    _triggerChannel.Writer.TryWrite($"id:{id}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing websocket event");
        }

        return Task.CompletedTask;
    }

    // ─── Claiming (ARK→BTC) ───────────────────────────────────────

    internal async Task TryClaimBtcForChainSwap(ArkSwap swap, CancellationToken cancellationToken)
    {
        if (swap.SwapType != ArkSwapType.ChainArkToBtc)
            return;

        var ephemeralKeyHex = swap.Get(SwapMetadata.EphemeralKey);
        var boltzResponseJson = swap.Get(SwapMetadata.BoltzResponse);
        var preimageHex = swap.Get(SwapMetadata.Preimage);
        var btcAddress = swap.Get(SwapMetadata.BtcAddress);

        if (string.IsNullOrEmpty(ephemeralKeyHex) ||
            string.IsNullOrEmpty(boltzResponseJson) ||
            string.IsNullOrEmpty(preimageHex) ||
            string.IsNullOrEmpty(btcAddress))
        {
            _logger?.LogWarning("Swap {SwapId}: missing chain swap metadata for BTC claim", swap.SwapId);
            return;
        }

        try
        {
            var response = BoltzSwapService.DeserializeChainResponse(boltzResponseJson);
            if (response == null)
            {
                _logger?.LogWarning("Swap {SwapId}: failed to deserialize Boltz response", swap.SwapId);
                return;
            }

            var claimDetails = response.ClaimDetails;
            if (claimDetails?.SwapTree == null || claimDetails.ServerPublicKey == null)
            {
                _logger?.LogWarning("Swap {SwapId}: no BTC claim details (swapTree or serverPublicKey is null)", swap.SwapId);
                return;
            }

            var ephemeralKey = new Key(Convert.FromHexString(ephemeralKeyHex));
            var ecPrivKey = ECPrivKey.Create(ephemeralKey.ToBytes());
            var userPubKey = ecPrivKey.CreatePubKey();
            var boltzPubKey = ECPubKey.Create(Convert.FromHexString(claimDetails.ServerPublicKey));

            var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

            var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                claimDetails.SwapTree, userPubKey, boltzPubKey,
                claimDetails.LockupAddress, serverInfo.Network);
            var btcDest = BitcoinAddress.Create(btcAddress, serverInfo.Network);

            // Get the lockup transaction from Boltz's status response
            var swapStatus = await _boltzClient.GetSwapStatusAsync(swap.SwapId, cancellationToken);
            if (swapStatus?.Transaction?.Hex == null)
            {
                _logger?.LogDebug("Swap {SwapId}: lockup tx hex not yet available", swap.SwapId);
                return;
            }

            // Parse the lockup tx and find the output matching the HTLC address
            var lockupTx = Transaction.Parse(swapStatus.Transaction.Hex, serverInfo.Network);
            var lockupScript = BitcoinAddress.Create(claimDetails.LockupAddress, serverInfo.Network).ScriptPubKey;
            var vout = -1;
            for (var i = 0; i < lockupTx.Outputs.Count; i++)
            {
                if (lockupTx.Outputs[i].ScriptPubKey == lockupScript)
                {
                    vout = i;
                    break;
                }
            }

            if (vout < 0)
            {
                _logger?.LogWarning("Swap {SwapId}: no output matching HTLC address {Address}", swap.SwapId, claimDetails.LockupAddress);
                return;
            }

            var outpoint = new OutPoint(lockupTx.GetHash(), vout);
            var prevOut = lockupTx.Outputs[vout];

            // Build unsigned claim tx
            var feeSats = 250L;
            var unsignedClaimTx = BtcTransactionBuilder.BuildKeyPathClaimTx(outpoint, prevOut, btcDest, feeSats);

            Transaction signedTx;
            try
            {
                _logger?.LogInformation("Swap {SwapId}: attempting MuSig2 cooperative BTC claim", swap.SwapId);
                signedTx = await _chainSwapMusig.CooperativeClaimAsync(
                    swap.SwapId, preimageHex, unsignedClaimTx, prevOut, 0,
                    ecPrivKey, boltzPubKey, spendInfo, cancellationToken);
            }
            catch (Exception coopEx)
            {
                _logger?.LogWarning(coopEx, "Swap {SwapId}: MuSig2 cooperative claim failed, falling back to script-path", swap.SwapId);

                // Fallback: script-path claim with preimage
                var claimLeaf = BtcHtlcScripts.GetClaimLeaf(claimDetails.SwapTree);
                var preimageBytes = Convert.FromHexString(preimageHex);
                BtcTransactionBuilder.SignScriptPathClaim(
                    unsignedClaimTx, 0, prevOut, spendInfo, claimLeaf,
                    preimageBytes, ephemeralKey);
                signedTx = unsignedClaimTx;
            }

            // Broadcast the signed claim transaction
            var broadcastResult = await _boltzClient.BroadcastBtcTransactionAsync(
                new BroadcastRequest { Hex = signedTx.ToHex() }, cancellationToken);

            _logger?.LogInformation("Swap {SwapId}: BTC claimed! txid={TxId}", swap.SwapId, broadcastResult.Id);

            await _swapsStorage.SaveSwap(swap.WalletId,
                swap with { Status = ArkSwapStatus.Settled, UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Swap {SwapId}: error claiming BTC", swap.SwapId);
        }
    }

    // ─── Cross-Signing (BTC→ARK) ──────────────────────────────────

    internal async Task TrySignBoltzBtcClaim(ArkSwap swap, CancellationToken cancellationToken)
    {
        if (swap.SwapType != ArkSwapType.ChainBtcToArk)
            return;

        // Only cross-sign once — avoid sending duplicate signatures on repeated polls
        if (swap.Get(SwapMetadata.CrossSigned) == "true")
            return;

        var ephemeralKeyHex = swap.Get(SwapMetadata.EphemeralKey);
        var boltzResponseJson = swap.Get(SwapMetadata.BoltzResponse);

        if (string.IsNullOrEmpty(ephemeralKeyHex) || string.IsNullOrEmpty(boltzResponseJson))
        {
            _logger?.LogWarning("Swap {SwapId}: missing chain swap metadata for cooperative BTC claim signing", swap.SwapId);
            return;
        }

        try
        {
            var response = BoltzSwapService.DeserializeChainResponse(boltzResponseJson);
            if (response == null)
            {
                _logger?.LogWarning("Swap {SwapId}: failed to deserialize Boltz response for cross-signing", swap.SwapId);
                return;
            }

            var lockupDetails = response.LockupDetails;
            if (lockupDetails?.SwapTree == null || lockupDetails.ServerPublicKey == null)
            {
                _logger?.LogWarning("Swap {SwapId}: no BTC lockup details (swapTree or serverPublicKey is null)", swap.SwapId);
                return;
            }

            var ephemeralKey = new Key(Convert.FromHexString(ephemeralKeyHex));
            var ecPrivKey = ECPrivKey.Create(ephemeralKey.ToBytes());
            var userPubKey = ecPrivKey.CreatePubKey();
            var boltzPubKey = ECPubKey.Create(Convert.FromHexString(lockupDetails.ServerPublicKey));

            var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

            var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                lockupDetails.SwapTree, userPubKey, boltzPubKey,
                lockupDetails.LockupAddress, serverInfo.Network);

            _logger?.LogInformation("Swap {SwapId}: providing cooperative MuSig2 cross-signature for Boltz BTC claim", swap.SwapId);
            await _chainSwapMusig.CrossSignBoltzClaimAsync(
                swap.SwapId, ecPrivKey, boltzPubKey, spendInfo, cancellationToken);

            _logger?.LogInformation("Swap {SwapId}: cooperative cross-signature sent successfully", swap.SwapId);

            // Mark as cross-signed to avoid sending duplicate signatures
            var metadata = new Dictionary<string, string>(swap.Metadata ?? [])
            {
                [SwapMetadata.CrossSigned] = "true"
            };
            await _swapsStorage.SaveSwap(swap.WalletId,
                swap with { Metadata = metadata, UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Non-critical: Boltz can still claim via script-path with the preimage
            _logger?.LogWarning(ex, "Swap {SwapId}: cooperative cross-signing failed (non-critical, Boltz will use script-path)", swap.SwapId);
        }
    }

    // ─── Swap Creation (delegated from SwapsManagementService) ────

    internal BoltzSwapService BoltzService => _boltzService;

    // ─── Swap Restoration ──────────────────────────────────────────

    internal async Task<RestorableSwap[]> RestoreSwapsFromBoltzAsync(
        string[] publicKeys, CancellationToken ct)
    {
        return (await _boltzClient.RestoreSwapsAsync(publicKeys, ct))
            .Where(swap => swap.From == "ARK" || swap.To == "ARK").ToArray();
    }

    internal async Task<SubmarineRefundResponse> RefundSubmarineSwapAsync(
        string swapId, SubmarineRefundRequest request, CancellationToken ct)
    {
        return await _boltzClient.RefundSubmarineSwapAsync(swapId, request, ct);
    }

    // ─── Disposal ──────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _logger?.LogInformation("Disposing Boltz swap provider");

        await _shutdownCts.CancelAsync();

        try
        {
            if (_cacheTask is not null)
                await _cacheTask;
        }
        catch
        {
            // ignored
        }

        try
        {
            if (_routinePollTask is not null)
                await _routinePollTask;
        }
        catch
        {
            // ignored
        }

        try
        {
            if (_lastStreamTask is not null)
                await _lastStreamTask;
        }
        catch
        {
            // ignored
        }
    }
}
