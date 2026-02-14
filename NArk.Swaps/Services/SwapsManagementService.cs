using System.Collections.Concurrent;
using System.Threading.Channels;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NArk.Core.Helpers;
using NArk.Core.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Extensions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Restore;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Models;
using NArk.Core.Transport;
using NArk.Swaps.Utils;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using OutputDescriptorHelpers = NArk.Abstractions.Extensions.OutputDescriptorHelpers;

namespace NArk.Swaps.Services;

public class SwapsManagementService : IAsyncDisposable
{
    private readonly SpendingService _spendingService;
    private readonly IClientTransport _clientTransport;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IWalletProvider _walletProvider;
    private readonly ISwapStorage _swapsStorage;
    private readonly IContractService _contractService;
    private readonly IContractStorage _contractStorage;
    private readonly ISafetyService _safetyService;
    private readonly BoltzSwapService _boltzService;
    private readonly BoltzChainSwapService _boltzChainService;
    private readonly ChainSwapMusigSession _chainSwapMusig;
    private readonly BoltzClient _boltzClient;
    private readonly IChainTimeProvider _chainTimeProvider;
    private readonly TransactionHelpers.ArkTransactionBuilder _transactionBuilder;
    private readonly ILogger<SwapsManagementService>? _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<string> _triggerChannel = Channel.CreateUnbounded<string>();

    private HashSet<string> _swapsIdToWatch = [];
    private readonly ConcurrentDictionary<string, string> _swapIdsToScript = [];

    private Task? _cacheTask;
    private Task? _routinePollTask;

    private Task? _lastStreamTask;
    private CancellationTokenSource _restartCts = new();
    private Network? _network;
    private ECXOnlyPubKey? _serverKey;

    public SwapsManagementService(
        SpendingService spendingService,
        IClientTransport clientTransport,
        IVtxoStorage vtxoStorage,
        IWalletProvider walletProvider,
        ISwapStorage swapsStorage,
        IContractService contractService,
        IContractStorage contractStorage,
        ISafetyService safetyService,
        IIntentStorage intentStorage,
        BoltzClient boltzClient,
        IChainTimeProvider chainTimeProvider,
        ILogger<SwapsManagementService>? logger = null
    )
    {
        _spendingService = spendingService;
        _clientTransport = clientTransport;
        _vtxoStorage = vtxoStorage;
        _walletProvider = walletProvider;
        _swapsStorage = swapsStorage;
        _contractService = contractService;
        _contractStorage = contractStorage;
        _safetyService = safetyService;
        _boltzClient = boltzClient;
        _chainTimeProvider = chainTimeProvider;
        _logger = logger;
        _boltzService = new BoltzSwapService(
            _boltzClient,
            _clientTransport
        );
        _boltzChainService = new BoltzChainSwapService(
            _boltzClient,
            _clientTransport
        );
        _chainSwapMusig = new ChainSwapMusigSession(_boltzClient);
        _transactionBuilder =
            new TransactionHelpers.ArkTransactionBuilder(clientTransport, safetyService, walletProvider, intentStorage);

        swapsStorage.SwapsChanged += OnSwapsChanged;
        // It is possible to listen for vtxos on scripts and use them to figure out the state of swaps
        vtxoStorage.VtxosChanged += OnVtxosChanged;
    }

    private void OnVtxosChanged(object? sender, ArkVtxo e)
    {
        if (_network is null || _serverKey is null) return;

        try
        {
            if (_swapIdsToScript.TryGetValue(e.Script, out var id))
            {
                _triggerChannel.Writer.TryWrite($"id:{id}");
            }
        }
        catch
        {
            // ignored
        }
    }

    private void OnSwapsChanged(object? sender, ArkSwap swapChanged)
    {
        _triggerChannel.Writer.TryWrite($"id:{swapChanged.SwapId}");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Starting swap management service");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        _serverKey = OutputDescriptorHelpers.Extract(serverInfo.SignerKey).XOnlyPubKey;
        _network = serverInfo.Network;
        _routinePollTask = RoutinePoll(TimeSpan.FromMinutes(1), multiToken.Token);
        _cacheTask = DoUpdateStorage(multiToken.Token);
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

    private async Task PollSwapState(IEnumerable<string> idsToPoll, CancellationToken cancellationToken)
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
                _swapIdsToScript[swap.SwapId] = swap.ContractScript;

                // There's nothing after refunded, ignore...
                if (swap.Status is ArkSwapStatus.Refunded) continue;

                // If not refunded and status is refundable, start a coop refund
                if (swap.SwapType is ArkSwapType.Submarine && swap.Status is not ArkSwapStatus.Refunded &&
                    IsRefundableStatus(swapStatus.Status))
                {
                    _logger?.LogInformation("Swap {SwapId}: Boltz status '{BoltzStatus}' is refundable, initiating cooperative refund",
                        idToPoll, swapStatus.Status);
                    var newSwap =
                        swap with { Status = ArkSwapStatus.Failed, UpdatedAt = DateTimeOffset.Now };
                    await RequestRefundCooperatively(newSwap, cancellationToken);
                }

                // For ARK→BTC chain swaps: try to claim BTC when server has locked
                if (swap.SwapType is ArkSwapType.ChainArkToBtc &&
                    IsChainSwapClaimableStatus(swapStatus.Status))
                {
                    _logger?.LogInformation("Chain swap {SwapId}: server locked BTC ('{BoltzStatus}'), attempting claim",
                        idToPoll, swapStatus.Status);
                    await TryClaimBtcForChainSwap(swap, cancellationToken);
                }

                var newStatus = Map(swapStatus.Status);

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
                    _swapIdsToScript.Remove(swapWithNewStatus.SwapId, out _);
                    _swapsIdToWatch.Remove(swapWithNewStatus.SwapId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Swap {SwapId}: error polling state from Boltz", idToPoll);
            }
        }
    }

    private async Task RequestRefundCooperatively(ArkSwap swap, CancellationToken cancellationToken = default)
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
        // Use AwaitingFundsBeforeDeactivate so it auto-deactivates after receiving the refund
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

            await using var @lock =
                await _safetyService.LockKeyAsync($"contract::{contract.GetArkAddress().ScriptPubKey.ToHex()}",
                    cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Swap {SwapId}: cooperative refund failed, deactivating refund contract", swap.SwapId);
            //coop swap failed, let's not keep listening for something that will never happen
            await _contractStorage.SaveContract(
                refundAddress.ToEntity(swap.WalletId, activityState: ContractActivityState.Inactive),
                cancellationToken);
            throw;
        }
    }

    private static ArkSwapStatus Map(string status)
    {
        return status switch
        {
            "swap.created" or "invoice.set" => ArkSwapStatus.Pending,
            "invoice.failedToPay" or "invoice.expired" or "swap.expired" or "transaction.failed"
                or "transaction.refunded" =>
                ArkSwapStatus.Failed,
            "transaction.mempool" => ArkSwapStatus.Pending,
            "transaction.confirmed" or "invoice.settled" or "transaction.claimed" => ArkSwapStatus.Settled,
            // Chain swap specific statuses
            "transaction.server.mempool" or "transaction.server.confirmed"
                or "transaction.claim.pending" => ArkSwapStatus.Pending,
            _ => ArkSwapStatus.Unknown
        };
    }

    /// <summary>
    /// Checks if a chain swap status indicates the server has locked funds and we can claim.
    /// </summary>
    private static bool IsChainSwapClaimableStatus(string status)
    {
        return status is "transaction.server.mempool" or "transaction.server.confirmed";
    }


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
            // ignored
        }

        return Task.CompletedTask;
    }


    public async Task<string> InitiateSubmarineSwap(string walletId, BOLT11PaymentRequest invoice, bool autoPay = true,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Initiating submarine swap for wallet {WalletId}, autoPay={AutoPay}", walletId, autoPay);
        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

        var addressProvider = await _walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        var swap = await _boltzService.CreateSubmarineSwap(invoice,
            await addressProvider!.GetNextSigningDescriptor(cancellationToken),
            cancellationToken);
        await _contractService.ImportContract(walletId, swap.Contract,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"swap:{swap.Swap.Id}" },
            cancellationToken: cancellationToken);
        await _swapsStorage.SaveSwap(
            walletId,
            new ArkSwap(
                swap.Swap.Id,
                walletId,
                ArkSwapType.Submarine,
                invoice.ToString(),
                swap.Swap.ExpectedAmount,
                swap.Contract.GetArkAddress().ScriptPubKey.ToHex(),
                swap.Address.ToString(serverInfo.Network.ChainName == ChainName.Mainnet),
                ArkSwapStatus.Pending,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                invoice.Hash.ToString()
            ), cancellationToken);
        try
        {
            return autoPay
                ? (await _spendingService.Spend(walletId,
                    [new ArkTxOut(ArkTxOutType.Vtxo, swap.Swap.ExpectedAmount, swap.Address)], cancellationToken))
                .ToString()
                : swap.Swap.Id;
        }
        catch (Exception e)
        {
            await _swapsStorage.SaveSwap(
                walletId,
                new ArkSwap(
                    swap.Swap.Id,
                    walletId,
                    ArkSwapType.Submarine,
                    invoice.ToString(),
                    swap.Swap.ExpectedAmount,
                    swap.Contract.GetArkAddress().ScriptPubKey.ToHex(),
                    swap.Address.ToString(serverInfo.Network.ChainName == ChainName.Mainnet),
                    ArkSwapStatus.Failed,
                    e.ToString(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    invoice.Hash.ToString()
                ), cancellationToken);
            throw;
        }
    }

    public async Task<uint256> PayExistingSubmarineSwap(string walletId, string swapId,
        CancellationToken cancellationToken = default)
    {
        var swaps = await _swapsStorage.GetSwaps(walletIds: [walletId], swapIds: [swapId],
            cancellationToken: cancellationToken);
        var swap = swaps.FirstOrDefault()
                   ?? throw new InvalidOperationException($"Swap {swapId} not found");
        try
        {
            return await _spendingService.Spend(walletId,
                [new ArkTxOut(ArkTxOutType.Vtxo, swap.ExpectedAmount, ArkAddress.Parse(swap.Address))],
                cancellationToken);
        }
        catch (Exception e)
        {
            await _swapsStorage.SaveSwap(
                walletId,
                swap with
                {
                    Status = ArkSwapStatus.Failed,
                    FailReason = e.ToString(),
                    UpdatedAt = DateTimeOffset.UtcNow
                }, cancellationToken);
            throw;
        }
    }

    public async Task<string> InitiateReverseSwap(string walletId, CreateInvoiceParams invoiceParams,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Initiating reverse swap for wallet {WalletId}, amount={Amount}",
            walletId, invoiceParams.Amount);
        var addressProvider = await _walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        var destinationDescriptor = await addressProvider!.GetNextSigningDescriptor(cancellationToken);
        var revSwap =
            await _boltzService.CreateReverseSwap(
                invoiceParams,
                destinationDescriptor,
                cancellationToken
            );
        await _contractService.ImportContract(walletId, revSwap.Contract,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"swap:{revSwap.Swap.Id}" },
            cancellationToken: cancellationToken);
        await _swapsStorage.SaveSwap(
            walletId,
            new ArkSwap(
                revSwap.Swap.Id,
                walletId,
                ArkSwapType.ReverseSubmarine,
                revSwap.Swap.Invoice,
                (long)invoiceParams.Amount.ToUnit(LightMoneyUnit.Satoshi),
                revSwap.Contract.GetArkAddress().ScriptPubKey.ToHex(),
                revSwap.Swap.LockupAddress,
                ArkSwapStatus.Pending,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                new uint256(revSwap.Hash).ToString()
            ), cancellationToken);

        return revSwap.Swap.Invoice;
    }

    // Chain Swaps

    /// <summary>
    /// Initiates a BTC→ARK chain swap. Customer pays BTC on-chain, store receives Ark VTXOs.
    /// Returns the BTC lockup address where the customer should send BTC.
    /// </summary>
    public async Task<(string BtcAddress, string SwapId)> InitiateBtcToArkChainSwap(
        string walletId,
        long amountSats,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Initiating BTC→ARK chain swap for wallet {WalletId}, amount={Amount}",
            walletId, amountSats);

        var addressProvider = await _walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        var claimDescriptor = await addressProvider!.GetNextSigningDescriptor(cancellationToken);

        var result = await _boltzChainService.CreateBtcToArkSwapAsync(
            amountSats, claimDescriptor, cancellationToken);

        // Import the Ark VHTLC contract (Boltz will lock funds here)
        await _contractService.ImportContract(walletId, result.Contract,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"swap:{result.Swap.Id}" },
            cancellationToken: cancellationToken);

        // The BTC lockup address is in lockupDetails (where customer sends BTC)
        var btcAddress = result.Swap.LockupDetails?.LockupAddress
            ?? throw new InvalidOperationException("Missing BTC lockup address");

        var swap = new ArkSwap(
            result.Swap.Id,
            walletId,
            ArkSwapType.ChainBtcToArk,
            "", // No invoice for chain swaps
            amountSats,
            result.Contract.GetArkAddress().ScriptPubKey.ToHex(),
            btcAddress,
            ArkSwapStatus.Pending,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Convert.ToHexString(result.PreimageHash).ToLowerInvariant()
        )
        {
            Preimage = Convert.ToHexString(result.Preimage).ToLowerInvariant(),
            EphemeralKeyHex = Convert.ToHexString(result.EphemeralBtcKey.ToBytes()).ToLowerInvariant(),
            BoltzResponseJson = BoltzChainSwapService.SerializeResponse(result.Swap),
            BtcAddress = btcAddress
        };

        await _swapsStorage.SaveSwap(walletId, swap, cancellationToken);

        _logger?.LogInformation("BTC→ARK chain swap {SwapId} created, BTC lockup: {BtcAddress}",
            result.Swap.Id, btcAddress);

        return (btcAddress, result.Swap.Id);
    }

    /// <summary>
    /// Initiates an ARK→BTC chain swap. User sends Ark VTXOs, receives BTC on-chain.
    /// </summary>
    public async Task<string> InitiateArkToBtcChainSwap(
        string walletId,
        long amountSats,
        BitcoinAddress btcDestination,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Initiating ARK→BTC chain swap for wallet {WalletId}, amount={Amount}, dest={Dest}",
            walletId, amountSats, btcDestination);

        var addressProvider = await _walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        var refundDescriptor = await addressProvider!.GetNextSigningDescriptor(cancellationToken);

        var result = await _boltzChainService.CreateArkToBtcSwapAsync(
            amountSats, refundDescriptor, btcDestination, cancellationToken);

        // Import the Ark VHTLC contract (we will lock our funds here)
        await _contractService.ImportContract(walletId, result.Contract,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"swap:{result.Swap.Id}" },
            cancellationToken: cancellationToken);

        var arkAddress = result.Contract.GetArkAddress();
        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

        var swap = new ArkSwap(
            result.Swap.Id,
            walletId,
            ArkSwapType.ChainArkToBtc,
            "", // No invoice for chain swaps
            amountSats,
            arkAddress.ScriptPubKey.ToHex(),
            arkAddress.ToString(serverInfo.Network.ChainName == ChainName.Mainnet),
            ArkSwapStatus.Pending,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Convert.ToHexString(result.PreimageHash).ToLowerInvariant()
        )
        {
            Preimage = Convert.ToHexString(result.Preimage).ToLowerInvariant(),
            EphemeralKeyHex = Convert.ToHexString(result.EphemeralBtcKey.ToBytes()).ToLowerInvariant(),
            BoltzResponseJson = BoltzChainSwapService.SerializeResponse(result.Swap),
            BtcAddress = btcDestination.ToString()
        };

        await _swapsStorage.SaveSwap(walletId, swap, cancellationToken);

        // Auto-pay: send Ark VTXOs to the lockup address
        try
        {
            var lockupAmount = result.Swap.LockupDetails?.Amount ?? amountSats;
            await _spendingService.Spend(walletId,
                [new ArkTxOut(ArkTxOutType.Vtxo, lockupAmount, arkAddress)], cancellationToken);
        }
        catch (Exception e)
        {
            await _swapsStorage.SaveSwap(walletId,
                swap with { Status = ArkSwapStatus.Failed, FailReason = e.ToString(), UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken);
            throw;
        }

        _logger?.LogInformation("ARK→BTC chain swap {SwapId} created, Ark locked", result.Swap.Id);
        return result.Swap.Id;
    }

    /// <summary>
    /// Attempts cooperative MuSig2 claim of BTC for an ARK→BTC chain swap.
    /// Called when Boltz has locked BTC and we need to claim it.
    /// </summary>
    private async Task TryClaimBtcForChainSwap(ArkSwap swap, CancellationToken cancellationToken)
    {
        if (swap.SwapType != ArkSwapType.ChainArkToBtc)
            return;

        if (string.IsNullOrEmpty(swap.EphemeralKeyHex) ||
            string.IsNullOrEmpty(swap.BoltzResponseJson) ||
            string.IsNullOrEmpty(swap.Preimage))
        {
            _logger?.LogWarning("Chain swap {SwapId}: missing data for BTC claim", swap.SwapId);
            return;
        }

        try
        {
            var response = BoltzChainSwapService.DeserializeResponse(swap.BoltzResponseJson);
            if (response == null)
            {
                _logger?.LogError("Chain swap {SwapId}: failed to deserialize Boltz response", swap.SwapId);
                return;
            }

            var claimDetails = response.ClaimDetails;
            if (claimDetails?.SwapTree == null || claimDetails.ServerPublicKey == null)
            {
                _logger?.LogWarning("Chain swap {SwapId}: no BTC claim details available", swap.SwapId);
                return;
            }

            var ephemeralKey = new Key(Convert.FromHexString(swap.EphemeralKeyHex));
            var ecPrivKey = ECPrivKey.Create(ephemeralKey.ToBytes());
            var userPubKey = ecPrivKey.CreatePubKey();
            var boltzPubKey = ECPubKey.Create(Convert.FromHexString(claimDetails.ServerPublicKey));

            var spendInfo = BtcHtlcScripts.ReconstructTaprootSpendInfo(
                claimDetails.SwapTree, userPubKey, boltzPubKey);

            var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
            var btcDest = BitcoinAddress.Create(swap.BtcAddress!, serverInfo.Network);

            // Get the lockup transaction from Boltz's status response
            var swapStatus = await _boltzClient.GetSwapStatusAsync(swap.SwapId, cancellationToken);
            if (swapStatus?.Transaction?.Hex == null)
            {
                _logger?.LogWarning("Chain swap {SwapId}: lockup transaction hex not yet available", swap.SwapId);
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
                _logger?.LogError("Chain swap {SwapId}: no output matching HTLC address {Address} in lockup tx",
                    swap.SwapId, claimDetails.LockupAddress);
                return;
            }

            var outpoint = new OutPoint(lockupTx.GetHash(), vout);
            var prevOut = lockupTx.Outputs[vout];

            // Build unsigned claim tx
            var feeSats = 250L;
            var unsignedClaimTx = BtcTransactionBuilder.BuildKeyPathClaimTx(outpoint, prevOut, btcDest, feeSats);

            // Cooperative MuSig2 claim
            var signedTx = await _chainSwapMusig.CooperativeClaimAsync(
                swap.SwapId, swap.Preimage, unsignedClaimTx, prevOut, 0,
                ecPrivKey, boltzPubKey, spendInfo, cancellationToken);

            // Broadcast the signed claim transaction
            var broadcastResult = await _boltzClient.BroadcastBtcTransactionAsync(
                new BroadcastRequest { Hex = signedTx.ToHex() }, cancellationToken);

            _logger?.LogInformation("Chain swap {SwapId}: BTC claimed, txid={TxId}",
                swap.SwapId, broadcastResult.Id);

            await _swapsStorage.SaveSwap(swap.WalletId,
                swap with { Status = ArkSwapStatus.Settled, UpdatedAt = DateTimeOffset.UtcNow },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Chain swap {SwapId}: error attempting BTC claim", swap.SwapId);
        }
    }

    // Swap Restoration

    /// <summary>
    /// Restores swaps from Boltz for the given descriptors.
    /// Caller determines which descriptors to pass (current key, all used indexes, etc.)
    /// </summary>
    /// <param name="walletId">The wallet identifier to associate restored swaps with.</param>
    /// <param name="descriptors">Array of output descriptors to search for in swaps.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of restored swaps that were not previously known.</returns>
    public async Task<IReadOnlyList<ArkSwap>> RestoreSwaps(
        string walletId,
        OutputDescriptor[] descriptors,
        CancellationToken cancellationToken = default)
    {
        if (descriptors.Length == 0)
            return [];

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

        // Extract public keys from all descriptors
        var publicKeys = descriptors
            .Select(d => OutputDescriptorHelpers.Extract(d).PubKey?.ToBytes()?.ToHexStringLower())
            .Where(s => s is not null)
            .Select(s => s!)
            .Distinct()
            .ToArray();

        var restoredSwaps = (await _boltzClient.RestoreSwapsAsync(publicKeys, cancellationToken))
            .Where(swap => swap.From == "ARK" || swap.To == "ARK").ToArray();
        var results = new List<ArkSwap>();

        var existingSwapIds =
            (await _swapsStorage.GetSwaps(walletIds: [walletId], swapIds: restoredSwaps.Select(swap => swap.Id).ToArray(),
                cancellationToken: cancellationToken)).Select(swap => swap.SwapId);

        restoredSwaps = restoredSwaps.ExceptBy(existingSwapIds, swap => swap.Id).ToArray();
        foreach (var restored in restoredSwaps)
        {
            var swap = MapRestoredSwap(restored, walletId);
            if (swap == null)
                continue;

            // Try to reconstruct and import the VHTLC contract
            var contract = ReconstructContract(restored, serverInfo, descriptors);
            if (contract != null)
            {
                // Update swap with contract script
                swap = swap with { ContractScript = contract.GetArkAddress().ScriptPubKey.ToHex() };

                await _contractService.ImportContract(
                    walletId,
                    contract,
                    ContractActivityState.Active,
                    metadata: new Dictionary<string, string> { ["Source"] = $"swap:{restored.Id}" },
                    cancellationToken: cancellationToken);
            }

            await _swapsStorage.SaveSwap(walletId, swap, cancellationToken);
            results.Add(swap);
        }

        return results;
    }

    private ArkSwap? MapRestoredSwap(RestorableSwap restored, string walletId)
    {
        var swapType = restored.Type switch
        {
            "reverse" => ArkSwapType.ReverseSubmarine,
            "submarine" => ArkSwapType.Submarine,
            _ => (ArkSwapType?)null
        };

        if (swapType == null)
            return null;

        var details = restored.Details;
        if (details == null)
            return null;

        return new ArkSwap(
            SwapId: restored.Id,
            WalletId: walletId,
            SwapType: swapType.Value,
            Invoice: "", // Not available from restore - needs enrichment
            ExpectedAmount: details.Amount ?? 0,
            ContractScript: "", // Will be updated after contract reconstruction
            Address: details.LockupAddress,
            Status: Map(restored.Status),
            FailReason: null,
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(restored.CreatedAt),
            UpdatedAt: DateTimeOffset.UtcNow,
            Hash: restored.PreimageHash ?? ""
        );
    }

    private VHTLCContract? ReconstructContract(
        RestorableSwap restored,
        ArkServerInfo serverInfo,
        OutputDescriptor[] descriptors)
    {
        var details = restored.Details;
        if (details?.Tree == null)
            return null;

        try
        {
            // Extract timelocks from tree leaves
            var refundLocktime = ScriptParser.ExtractAbsoluteTimelock(
                details.Tree.RefundWithoutBoltzLeaf?.Output);
            var unilateralClaimDelay = ScriptParser.ExtractRelativeTimelock(
                details.Tree.UnilateralClaimLeaf?.Output);
            var unilateralRefundDelay = ScriptParser.ExtractRelativeTimelock(
                details.Tree.UnilateralRefundLeaf?.Output);
            var unilateralRefundWithoutBoltzDelay = ScriptParser.ExtractRelativeTimelock(
                details.Tree.UnilateralRefundWithoutBoltzLeaf?.Output);

            // Validate we have the necessary timelocks
            if (refundLocktime == null || unilateralClaimDelay == null ||
                unilateralRefundDelay == null || unilateralRefundWithoutBoltzDelay == null)
            {
                return null;
            }

            // Parse preimage hash
            uint160? hash = null;
            if (!string.IsNullOrEmpty(restored.PreimageHash))
            {
                // Boltz uses SHA256 for preimage hash, we need RIPEMD160(SHA256(preimage))
                // The preimageHash from restore is the SHA256 hash
                var sha256Hash = Convert.FromHexString(restored.PreimageHash);
                hash = new uint160(NBitcoin.Crypto.Hashes.RIPEMD160(sha256Hash), false);
            }

            if (hash == null)
                return null;

            // Determine sender and receiver based on swap type
            OutputDescriptor sender;
            OutputDescriptor receiver;

            if (restored.IsReverseSwap)
            {
                // Reverse swap: we are the receiver (claiming)
                sender = KeyExtensions.ParseOutputDescriptor(details.ServerPublicKey, serverInfo.Network);
                receiver = FindMatchingDescriptor(descriptors, details) ?? descriptors[0];
            }
            else
            {
                // Submarine swap: we are the sender (refunding)
                sender = FindMatchingDescriptor(descriptors, details) ?? descriptors[0];
                receiver = KeyExtensions.ParseOutputDescriptor(details.ServerPublicKey, serverInfo.Network);
            }

            return new VHTLCContract(
                server: serverInfo.SignerKey,
                sender: sender,
                receiver: receiver,
                hash: hash,
                refundLocktime: refundLocktime.Value,
                unilateralClaimDelay: unilateralClaimDelay.Value,
                unilateralRefundDelay: unilateralRefundDelay.Value,
                unilateralRefundWithoutReceiverDelay: unilateralRefundWithoutBoltzDelay.Value
            );
        }
        catch
        {
            return null;
        }
    }

    private static OutputDescriptor? FindMatchingDescriptor(
        OutputDescriptor[] descriptors,
        SwapDetails details)
    {
        // If keyIndex is provided, try to find the matching descriptor
        if (details.KeyIndex.HasValue && details.KeyIndex.Value < descriptors.Length)
        {
            return descriptors[details.KeyIndex.Value];
        }

        // Return first descriptor as fallback
        return descriptors.Length > 0 ? descriptors[0] : null;
    }

    // Enrichment Methods

    /// <summary>
    /// Enriches a restored reverse swap with the preimage needed for claiming.
    /// Validates the preimage matches the stored hash before updating.
    /// </summary>
    /// <param name="swapId">The swap ID to enrich.</param>
    /// <param name="preimage">The preimage bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task EnrichReverseSwapPreimage(
        string swapId,
        byte[] preimage,
        CancellationToken cancellationToken = default)
    {
        await using var @lock = await _safetyService.LockKeyAsync($"swap::{swapId}", cancellationToken);

        var swaps = await _swapsStorage.GetSwaps(swapIds: [swapId], cancellationToken: cancellationToken);
        var swap = swaps.FirstOrDefault()
                   ?? throw new InvalidOperationException($"Swap {swapId} not found");
        if (swap.SwapType != ArkSwapType.ReverseSubmarine)
            throw new InvalidOperationException("Preimage enrichment only valid for reverse swaps");

        // Validate preimage matches hash (SHA256 for Boltz)
        var computedHash = NBitcoin.Crypto.Hashes.SHA256(preimage).ToHexStringLower();
        if (!string.Equals(computedHash, swap.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Preimage does not match stored hash");

        // Update contract with preimage for claiming
        var contracts = await _contractStorage.GetContracts(
            walletIds: [swap.WalletId], scripts: [swap.ContractScript], cancellationToken: cancellationToken);
        var contractEntity = contracts.SingleOrDefault(c => c.Type == VHTLCContract.ContractType);
        if (contractEntity == null)
            throw new InvalidOperationException("VHTLC contract not found for swap");

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        var contract = VHTLCContract.Parse(contractEntity.AdditionalData, serverInfo.Network) as VHTLCContract;
        if (contract == null)
            throw new InvalidOperationException("Failed to parse VHTLC contract");

        if (contract.Server == null)
            throw new InvalidOperationException("Server key is required for VHTLC contract");

        // Re-create contract with preimage and save
        var enrichedContract = new VHTLCContract(
            contract.Server, contract.Sender, contract.Receiver, preimage,
            contract.RefundLocktime, contract.UnilateralClaimDelay,
            contract.UnilateralRefundDelay, contract.UnilateralRefundWithoutReceiverDelay);

        await _contractStorage.SaveContract(
            enrichedContract.ToEntity(swap.WalletId, null, contractEntity.CreatedAt, ContractActivityState.Active),
            cancellationToken);
    }

    /// <summary>
    /// Enriches a restored submarine swap with the invoice.
    /// Validates the invoice payment hash matches the stored hash.
    /// </summary>
    /// <param name="swapId">The swap ID to enrich.</param>
    /// <param name="invoice">The BOLT11 invoice string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task EnrichSubmarineSwapInvoice(
        string swapId,
        string invoice,
        CancellationToken cancellationToken = default)
    {
        await using var @lock = await _safetyService.LockKeyAsync($"swap::{swapId}", cancellationToken);

        var swaps = await _swapsStorage.GetSwaps(swapIds: [swapId], cancellationToken: cancellationToken);
        var swap = swaps.FirstOrDefault()
                   ?? throw new InvalidOperationException($"Swap {swapId} not found");
        if (swap.SwapType != ArkSwapType.Submarine)
            throw new InvalidOperationException("Invoice enrichment only valid for submarine swaps");

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        var bolt11 = BOLT11PaymentRequest.Parse(invoice, serverInfo.Network);
        if (bolt11.PaymentHash == null)
            throw new InvalidOperationException("Invoice does not contain payment hash");

        // Validate invoice payment hash matches stored hash
        var invoiceHashHex = bolt11.PaymentHash.ToString();
        if (!string.Equals(invoiceHashHex, swap.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invoice payment hash does not match stored hash");

        // Update swap with invoice
        var enrichedSwap = swap with { Invoice = invoice, UpdatedAt = DateTimeOffset.UtcNow };
        await _swapsStorage.SaveSwap(swap.WalletId, enrichedSwap, cancellationToken);
    }

    private static bool IsRefundableStatus(string status)
    {
        // Statuses that indicate a submarine swap can be cooperatively refunded
        return status switch
        {
            "invoice.failedToPay" => true,
            "invoice.expired" => true,
            "swap.expired" => true,
            "transaction.lockupFailed" => true,
            _ => false
        };
    }

    public async ValueTask DisposeAsync()
    {
        _logger?.LogInformation("Disposing swap management service");
        _swapsStorage.SwapsChanged -= OnSwapsChanged;

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