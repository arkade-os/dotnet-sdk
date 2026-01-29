using System.Collections.Concurrent;
using System.Threading.Channels;
using BTCPayServer.Lightning;
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
using NArk.Swaps.Boltz.Models.Restore;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Helpers;
using NArk.Swaps.Models;
using NArk.Core.Transport;
using NArk.Swaps.Utils;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

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
    private readonly BoltzClient _boltzClient;
    private readonly IChainTimeProvider _chainTimeProvider;
    private readonly TransactionHelpers.ArkTransactionBuilder _transactionBuilder;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<string> _triggerChannel = Channel.CreateUnbounded<string>();

    private HashSet<string> _swapsIdToWatch = [];
    private ConcurrentDictionary<string, string> _swapAddressToIds = [];

    private Task? _cacheTask;

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
        IChainTimeProvider chainTimeProvider
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
        _boltzService = new BoltzSwapService(
            _boltzClient,
            _clientTransport
        );
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
            var vtxoAddress = ArkAddress
                .FromScriptPubKey(Script.FromHex(e.Script), _serverKey)
                .ToString(_network.ChainName == ChainName.Mainnet);
            if (_swapAddressToIds.TryGetValue(vtxoAddress, out var id))
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
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        _serverKey = serverInfo.SignerKey.Extract().XOnlyPubKey;
        _network = serverInfo.Network;

        _cacheTask = DoUpdateStorage(multiToken.Token);
        _triggerChannel.Writer.TryWrite("");
    }

    private async Task DoUpdateStorage(CancellationToken cancellationToken)
    {
        await foreach (var eventDetails in _triggerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            if (eventDetails.StartsWith("id:"))
            {
                var swapId = eventDetails[3..];

                // If we already monitor this swap, no need to restart websocket
                if (_swapsIdToWatch.Contains(swapId))
                {
                    await PollSwapState([swapId], cancellationToken);
                }
                else
                {
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
                    await _swapsStorage.GetSwaps(null,null, true, cancellationToken);
                var newSwapIdSet =
                    activeSwaps.Select(s => s.SwapId).ToHashSet();

                if (_swapsIdToWatch.SetEquals(newSwapIdSet))
                    continue;

                await PollSwapState(newSwapIdSet.Except(_swapsIdToWatch), cancellationToken);

                _swapsIdToWatch = newSwapIdSet;

                var newRestartCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                _lastStreamTask = DoStatusCheck(newSwapIdSet, newRestartCts.Token);
                await _restartCts.CancelAsync();
                _restartCts = newRestartCts;
            }
        }
    }

    private async Task PollSwapState(IEnumerable<string> idsToPoll, CancellationToken cancellationToken)
    {
        foreach (var idToPoll in idsToPoll)
        {
            var swapStatus = await _boltzClient.GetSwapStatusAsync(idToPoll, _shutdownCts.Token);
            if (swapStatus?.Status is null) continue;

            await using var @lock = await _safetyService.LockKeyAsync($"swap::{idToPoll}", cancellationToken);
            var swap = await _swapsStorage.GetSwap(idToPoll, cancellationToken);
            _swapAddressToIds[swap.SwapId] = swap.Address;

            // There's nothing after refunded, ignore...
            if (swap.Status is ArkSwapStatus.Refunded) continue;

            // If not refunded and status is refundable, start a coop refund
            if (swap.SwapType is ArkSwapType.Submarine && swap.Status is not ArkSwapStatus.Refunded && IsRefundableStatus(swapStatus.Status))
            {
                var newSwap =
                    swap with { Status = ArkSwapStatus.Failed, UpdatedAt = DateTimeOffset.Now };
                await RequestRefundCooperatively(newSwap, cancellationToken);
            }

            var newStatus = Map(swapStatus.Status);

            if (swap.Status == newStatus) continue;

            var swapWithNewStatus =
                swap with { Status = newStatus, UpdatedAt = DateTimeOffset.Now };

            await _swapsStorage.SaveSwap(swap.WalletId,
                swapWithNewStatus, cancellationToken: cancellationToken);

            if (swapWithNewStatus.Status is ArkSwapStatus.Settled or ArkSwapStatus.Refunded)
            {
                _swapAddressToIds.Remove(swapWithNewStatus.SwapId, out _);
                _swapsIdToWatch.Remove(swapWithNewStatus.SwapId);
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
            await _contractStorage.LoadContractsByScripts([swap.ContractScript], [swap.WalletId], cancellationToken);

        var matchedSwapContractForSwapWallet = matchedSwapContracts.Single(entity => entity.Type == VHTLCContract.ContractType);

        // Parse the VHTLC contract
        if (ArkContractParser.Parse(matchedSwapContractForSwapWallet.Type, matchedSwapContractForSwapWallet.AdditionalData, serverInfo.Network) is not VHTLCContract contract)
        {
            throw new InvalidOperationException("Failed to parse VHTLC contract for refund");
        }

        // Get VTXOs for this contract
        var vtxos = await _vtxoStorage.GetVtxos(VtxoFilter.ByScripts(swap.ContractScript),
            cancellationToken);
        if (vtxos.Count == 0)
        {
            // logger.LogWarning("No VTXOs found for submarine swap {SwapId} refund", swap.SwapId);
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
                ContractActivityState.AwaitingFundsBeforeDeactivate, cancellationToken);
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

            await using var @lock = await _safetyService.LockKeyAsync($"contract::{contract.GetArkAddress().ScriptPubKey.ToHex()}", cancellationToken);
        }
        catch (Exception)
        {
            //coop swap failed, let's not keep listening for something that will never happen
            await _contractStorage.SaveContract(refundAddress.ToEntity(swap.WalletId, activityState: ContractActivityState.Inactive), cancellationToken);
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
            _ => ArkSwapStatus.Unknown
        };
    }


    private async Task DoStatusCheck(HashSet<string> swapsIds, CancellationToken cancellationToken)
    {
        await using var websocketClient = new BoltzWebsocketClient(_boltzClient.DeriveWebSocketUri());
        websocketClient.OnAnyEventReceived += OnSwapEventReceived;
        try
        {
            await websocketClient.ConnectAsync(cancellationToken);
            await websocketClient.SubscribeAsync(swapsIds.ToArray(), cancellationToken);
            await websocketClient.WaitUntilDisconnected(cancellationToken);
        }
        finally
        {
            websocketClient.OnAnyEventReceived -= OnSwapEventReceived;
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
                    _triggerChannel.Writer.TryWrite($"id:{id}");
                }
            }
        }
        catch
        {
            // ignored
        }

        return Task.CompletedTask;
    }


    public async Task<string> InitiateSubmarineSwap(string walletId, BOLT11PaymentRequest invoice, bool autoPay = true,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

        var addressProvider = await _walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        var swap = await _boltzService.CreateSubmarineSwap(invoice,
            await addressProvider!.GetNextSigningDescriptor(cancellationToken),
            cancellationToken);
        await _contractService.ImportContract(walletId, swap.Contract, ContractActivityState.AwaitingFundsBeforeDeactivate, cancellationToken: cancellationToken);
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
        var swap = await _swapsStorage.GetSwap(swapId, cancellationToken);
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
        var addressProvider = await _walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        var destinationDescriptor = await addressProvider!.GetNextSigningDescriptor(cancellationToken);
        var revSwap =
            await _boltzService.CreateReverseSwap(
                invoiceParams,
                destinationDescriptor,
                cancellationToken
            );
        await _contractService.ImportContract(walletId, revSwap.Contract, ContractActivityState.AwaitingFundsBeforeDeactivate, cancellationToken: cancellationToken);
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
            .Select(d => d.Extract().PubKey?.ToBytes()?.ToHexStringLower())
            .Where(s => s is not null)
            .Select(s => s!)
            .Distinct()
            .ToArray();

        var restoredSwaps = (await _boltzClient.RestoreSwapsAsync(publicKeys, cancellationToken))
            .Where(swap => swap.From == "ARK" || swap.To == "ARK").ToArray();
        var results = new List<ArkSwap>();

        var existingSwapIds =
            (await _swapsStorage.GetSwaps(walletId, restoredSwaps.Select(swap => swap.Id).ToArray(),
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
                    cancellationToken);
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

        var swap = await _swapsStorage.GetSwap(swapId, cancellationToken);
        if (swap.SwapType != ArkSwapType.ReverseSubmarine)
            throw new InvalidOperationException("Preimage enrichment only valid for reverse swaps");

        // Validate preimage matches hash (SHA256 for Boltz)
        var computedHash = NBitcoin.Crypto.Hashes.SHA256(preimage).ToHexStringLower();
        if (!string.Equals(computedHash, swap.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Preimage does not match stored hash");

        // Update contract with preimage for claiming
        var contracts = await _contractStorage.LoadContractsByScripts(
            [swap.ContractScript], [swap.WalletId], cancellationToken);
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

        var swap = await _swapsStorage.GetSwap(swapId, cancellationToken);
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
            if (_lastStreamTask is not null)
                await _lastStreamTask;
        }
        catch
        {
            // ignored
        }
    }
}