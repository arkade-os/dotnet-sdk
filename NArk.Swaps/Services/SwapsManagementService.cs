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
using NArk.Core.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Models.Restore;
using NArk.Swaps.Models;
using NArk.Core.Transport;
using NArk.Swaps.Utils;
using NBitcoin;
using NBitcoin.Scripting;
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
    private readonly IBitcoinBlockchain _chainTimeProvider;
    private readonly ILogger<SwapsManagementService>? _logger;
    private readonly IReadOnlyList<ISwapProvider> _providers;

    // Convenience accessor for backward-compatible Boltz-specific methods
    private readonly BoltzSwapProvider? _boltzProvider;

    public SwapsManagementService(
        IEnumerable<ISwapProvider> providers,
        SpendingService spendingService,
        IClientTransport clientTransport,
        IVtxoStorage vtxoStorage,
        IWalletProvider walletProvider,
        ISwapStorage swapsStorage,
        IContractService contractService,
        IContractStorage contractStorage,
        ISafetyService safetyService,
        IIntentStorage intentStorage,
        IBitcoinBlockchain chainTimeProvider,
        ILogger<SwapsManagementService>? logger = null)
    {
        _providers = providers.ToList();
        _spendingService = spendingService;
        _clientTransport = clientTransport;
        _vtxoStorage = vtxoStorage;
        _walletProvider = walletProvider;
        _swapsStorage = swapsStorage;
        _contractService = contractService;
        _contractStorage = contractStorage;
        _safetyService = safetyService;
        _chainTimeProvider = chainTimeProvider;
        _logger = logger;

        // Find the Boltz provider for backward-compatible methods
        _boltzProvider = _providers.OfType<BoltzSwapProvider>().FirstOrDefault();

        // Wire up events: route storage and VTXO changes to the appropriate provider
        swapsStorage.SwapsChanged += OnSwapsChanged;
        vtxoStorage.VtxosChanged += OnVtxosChanged;
    }

    // ─── Provider Resolution ───────────────────────────────────────

    /// <summary>
    /// Resolves the best provider for a given route, optionally preferring a specific provider.
    /// </summary>
    public ISwapProvider ResolveProvider(SwapRoute route, string? preferredProviderId = null)
    {
        var candidates = _providers.Where(p => p.SupportsRoute(route)).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No provider supports route {route}");
        if (preferredProviderId != null)
        {
            var preferred = candidates.FirstOrDefault(p => p.ProviderId == preferredProviderId);
            if (preferred != null) return preferred;
        }
        return candidates[0];
    }

    /// <summary>
    /// Returns all registered swap providers.
    /// </summary>
    public IReadOnlyList<ISwapProvider> Providers => _providers;

    // ─── Event Routing ─────────────────────────────────────────────

    private void OnVtxosChanged(object? sender, ArkVtxo e)
    {
        // Route to all providers via the interface — each provider decides
        // whether it cares (default ISwapProvider.NotifyVtxoChanged is a no-op).
        foreach (var provider in _providers)
            provider.NotifyVtxoChanged(e);
    }

    private void OnSwapsChanged(object? sender, ArkSwap swapChanged)
    {
        foreach (var provider in _providers)
            provider.NotifySwapChanged(swapChanged);
    }

    // ─── Lifecycle ─────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Starting swap management service with {Count} provider(s)", _providers.Count);
        foreach (var provider in _providers)
        {
            _logger?.LogInformation("Starting swap provider: {ProviderId} ({DisplayName})", provider.ProviderId, provider.DisplayName);
            await provider.StartAsync(cancellationToken);
        }
    }

    // ─── Generic Entry Points ──────────────────────────────────────

    /// <summary>
    /// Gets limits for a given swap route from the best matching provider.
    /// </summary>
    public async Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct)
    {
        var provider = ResolveProvider(route);
        return await provider.GetLimitsAsync(route, ct);
    }

    /// <summary>
    /// Gets a fee quote for a given swap route and amount.
    /// </summary>
    public async Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct)
    {
        var provider = ResolveProvider(route);
        return await provider.GetQuoteAsync(route, amount, ct);
    }

    /// <summary>
    /// Returns all routes supported across all registered providers.
    /// </summary>
    public async Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct)
    {
        var routes = new List<SwapRoute>();
        foreach (var provider in _providers)
        {
            var providerRoutes = await provider.GetAvailableRoutesAsync(ct);
            routes.AddRange(providerRoutes);
        }
        return routes.Distinct().ToList();
    }

    // ─── Backward-Compatible Public API ────────────────────────────

    public async Task<string> InitiateSubmarineSwap(string walletId, BOLT11PaymentRequest invoice, bool autoPay = true,
        CancellationToken cancellationToken = default)
    {
        using var _walletScope = _logger?.BeginScope(("WalletId", walletId));
        var boltz = GetBoltzProvider();

        _logger?.LogInformation("Initiating submarine swap for wallet {WalletId}, autoPay={AutoPay}", walletId, autoPay);
        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

        var addressProvider = await _walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        var swap = await boltz.BoltzService.CreateSubmarineSwap(invoice,
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
            )
            {
                ProviderId = BoltzSwapProvider.Id,
                Route = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning)
            }, cancellationToken);
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
                )
                {
                    ProviderId = BoltzSwapProvider.Id,
                    Route = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning)
                }, cancellationToken);
            throw;
        }
    }

    public async Task<uint256> PayExistingSubmarineSwap(string walletId, string swapId,
        CancellationToken cancellationToken = default)
    {
        using var _walletScope = _logger?.BeginScope(("WalletId", walletId));
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
        using var _walletScope = _logger?.BeginScope(("WalletId", walletId));
        var boltz = GetBoltzProvider();

        _logger?.LogInformation("Initiating reverse swap for wallet {WalletId}, amount={Amount}",
            walletId, invoiceParams.Amount);
        var addressProvider = await _walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        var destinationDescriptor = await addressProvider!.GetNextSigningDescriptor(cancellationToken);
        var revSwap =
            await boltz.BoltzService.CreateReverseSwap(
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
            )
            {
                ProviderId = BoltzSwapProvider.Id,
                Route = new SwapRoute(SwapAsset.BtcLightning, SwapAsset.ArkBtc)
            }, cancellationToken);

        return revSwap.Swap.Invoice;
    }

    // Chain Swaps

    /// <summary>
    /// Initiates a BTC to ARK chain swap. Customer pays BTC on-chain, store receives Ark VTXOs.
    /// Returns the BTC lockup address where the customer should send BTC.
    /// </summary>
    public async Task<(string BtcAddress, string SwapId, long ExpectedLockupSats)> InitiateBtcToArkChainSwap(
        string walletId,
        long amountSats,
        CancellationToken cancellationToken = default)
    {
        using var _walletScope = _logger?.BeginScope(("WalletId", walletId));
        var boltz = GetBoltzProvider();

        _logger?.LogInformation("Initiating BTC->ARK chain swap for wallet {WalletId}, amount={Amount}",
            walletId, amountSats);

        var addressProvider = await _walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        var claimDescriptor = await addressProvider!.GetNextSigningDescriptor(cancellationToken);

        var result = await boltz.BoltzService.CreateBtcToArkSwapAsync(
            amountSats, claimDescriptor, cancellationToken);

        var btcAddress = result.Swap.LockupDetails?.LockupAddress
            ?? throw new InvalidOperationException("Missing BTC lockup address");

        // Import VHTLC contract for sweeper to claim when VTXOs appear.
        var contract = result.Contract!;
        await _contractService.ImportContract(walletId, contract,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = $"swap:{result.Swap.Id}" },
            cancellationToken: cancellationToken);
        var contractScript = contract.GetArkAddress().ScriptPubKey.ToHex();

        var swap = new ArkSwap(
            result.Swap.Id,
            walletId,
            ArkSwapType.ChainBtcToArk,
            "",
            amountSats,
            contractScript,
            btcAddress,
            ArkSwapStatus.Pending,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Convert.ToHexString(result.PreimageHash).ToLowerInvariant()
        )
        {
            Metadata = new Dictionary<string, string>
            {
                [SwapMetadata.Preimage] = Convert.ToHexString(result.Preimage).ToLowerInvariant(),
                [SwapMetadata.EphemeralKey] = Convert.ToHexString(result.EphemeralBtcKey.ToBytes()).ToLowerInvariant(),
                [SwapMetadata.BoltzResponse] = BoltzSwapService.SerializeChainResponse(result.Swap),
                [SwapMetadata.BtcAddress] = btcAddress
            },
            ProviderId = BoltzSwapProvider.Id,
            Route = new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc)
        };

        await _swapsStorage.SaveSwap(walletId, swap, cancellationToken);

        var expectedLockupSats = result.Swap.LockupDetails!.Amount;
        _logger?.LogInformation("BTC->ARK chain swap {SwapId} created, BTC lockup: {BtcAddress}, expected: {Amount} sats",
            result.Swap.Id, btcAddress, expectedLockupSats);

        return (btcAddress, result.Swap.Id, expectedLockupSats);
    }

    /// <summary>
    /// Initiates an ARK to BTC chain swap. User sends Ark VTXOs, receives BTC on-chain.
    /// </summary>
    public async Task<string> InitiateArkToBtcChainSwap(
        string walletId,
        long amountSats,
        BitcoinAddress btcDestination,
        CancellationToken cancellationToken = default)
    {
        using var _walletScope = _logger?.BeginScope(("WalletId", walletId));
        var boltz = GetBoltzProvider();

        _logger?.LogInformation("Initiating ARK->BTC chain swap for wallet {WalletId}, amount={Amount}, dest={Dest}",
            walletId, amountSats, btcDestination);

        var addressProvider = await _walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        var refundDescriptor = await addressProvider!.GetNextSigningDescriptor(cancellationToken);

        // Extract pub key hex for Boltz API
        var extractedRefund = OutputDescriptorHelpers.Extract(refundDescriptor);
        var refundPubKeyHex = Convert.ToHexString(
            extractedRefund.PubKey?.ToBytes() ?? extractedRefund.XOnlyPubKey.ToBytes()).ToLowerInvariant();

        var result = await boltz.BoltzService.CreateArkToBtcSwapAsync(
            amountSats, refundPubKeyHex, cancellationToken);

        // Parse the Ark lockup address (Boltz's fulmine created the VHTLC)
        var arkLockupAddressStr = result.Swap.LockupDetails!.LockupAddress;
        var arkAddress = ArkAddress.Parse(arkLockupAddressStr);

        var swap = new ArkSwap(
            result.Swap.Id,
            walletId,
            ArkSwapType.ChainArkToBtc,
            "", // No invoice for chain swaps
            amountSats,
            arkLockupAddressStr, // Store ARK lockup address as identifier
            arkLockupAddressStr,
            ArkSwapStatus.Pending,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Convert.ToHexString(result.PreimageHash).ToLowerInvariant()
        )
        {
            Metadata = new Dictionary<string, string>
            {
                [SwapMetadata.Preimage] = Convert.ToHexString(result.Preimage).ToLowerInvariant(),
                [SwapMetadata.EphemeralKey] = Convert.ToHexString(result.EphemeralBtcKey.ToBytes()).ToLowerInvariant(),
                [SwapMetadata.BoltzResponse] = BoltzSwapService.SerializeChainResponse(result.Swap),
                [SwapMetadata.BtcAddress] = btcDestination.ToString()
            },
            ProviderId = BoltzSwapProvider.Id,
            Route = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain)
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

        _logger?.LogInformation("ARK->BTC chain swap {SwapId} created, Ark locked", result.Swap.Id);
        return result.Swap.Id;
    }

    // ─── Swap Recovery Inspection ──────────────────────────────────

    /// <summary>
    /// Diagnostic, side-effect-free inspection of a swap to determine
    /// whether funds are still recoverable from the on-chain / off-chain
    /// lockup. Useful for wallet UIs that want to surface "X sats
    /// recoverable" indicators on Failed swaps without committing to a
    /// recovery transaction. Refreshes the local VTXO snapshot from arkd
    /// so newly-arrived VTXOs at the swap script are visible to the call.
    /// </summary>
    /// <remarks>
    /// Mirrors arkade-os/boltz-swap's <c>inspectSubmarineRecovery</c> /
    /// <c>inspectChainSwapRecovery</c> APIs. The actual recovery happens
    /// automatically inside <c>BoltzSwapProvider.PollSwapState</c> when
    /// the swap reaches a refundable status — this method is purely a
    /// reporting helper.
    /// </remarks>
    public async Task<SwapRecoveryInfo> InspectSwapRecoveryAsync(
        string walletId, string swapId, CancellationToken ct = default)
    {
        var swap = (await _swapsStorage.GetSwaps(walletIds: [walletId], swapIds: [swapId], cancellationToken: ct))
            .FirstOrDefault();
        if (swap is null)
            return new SwapRecoveryInfo { SwapId = swapId, Status = SwapRecoveryStatus.SwapNotFound };

        if (swap.Status is ArkSwapStatus.Refunded)
            return new SwapRecoveryInfo { SwapId = swapId, Swap = swap, Status = SwapRecoveryStatus.AlreadyRefunded };
        if (swap.Status is ArkSwapStatus.Settled)
            return new SwapRecoveryInfo { SwapId = swapId, Swap = swap, Status = SwapRecoveryStatus.AlreadySettled };

        // A Pending swap is mid-flight, not stranded. VTXOs at its contract
        // script are the live lockup — reporting them as Recoverable would
        // make a wallet UI show "X sats stranded" for every active swap.
        // ScanRecoverableSwapsAsync filters Pending out before reaching here,
        // but this method is public and callers can hit it directly.
        if (swap.Status is ArkSwapStatus.Pending)
            return new SwapRecoveryInfo { SwapId = swapId, Swap = swap, Status = SwapRecoveryStatus.StillPending };

        // Refresh the local VTXO snapshot so a freshly-arrived VTXO at the
        // swap's contract script (e.g. user funded the lockup just before
        // this scan) shows up.
        if (!string.IsNullOrEmpty(swap.ContractScript))
        {
            try
            {
                await foreach (var fresh in _clientTransport.GetVtxoByScriptsAsSnapshot(
                                   new HashSet<string> { swap.ContractScript }, ct))
                {
                    await _vtxoStorage.UpsertVtxo(fresh, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new SwapRecoveryInfo
                {
                    SwapId = swapId, Swap = swap, Status = SwapRecoveryStatus.InspectionError,
                    Error = $"Failed to refresh VTXOs: {ex.Message}",
                };
            }
        }

        var vtxos = string.IsNullOrEmpty(swap.ContractScript)
            ? []
            : (await _vtxoStorage.GetVtxos(scripts: [swap.ContractScript], cancellationToken: ct))
              .Where(v => !v.IsSpent()).ToList();
        var amount = vtxos.Sum(v => (long)v.Amount);

        return new SwapRecoveryInfo
        {
            SwapId = swapId,
            Swap = swap,
            Status = vtxos.Count > 0 ? SwapRecoveryStatus.Recoverable : SwapRecoveryStatus.NoFunds,
            VtxoCount = vtxos.Count,
            AmountSats = amount,
        };
    }

    /// <summary>
    /// Bulk recovery scan: walks all locally-known non-pending swaps for
    /// the wallet and reports each swap's recovery state. The result is a
    /// snapshot suitable for "audit my swap history for stranded funds"
    /// flows after a wallet restore. Skips swaps still in
    /// <see cref="ArkSwapStatus.Pending"/> since those aren't recovery
    /// candidates yet.
    /// </summary>
    /// <remarks>
    /// Mirrors arkade-os/boltz-swap's <c>scanRecoverableSubmarineSwaps</c>
    /// but covers all swap types (submarine, reverse, chain in both
    /// directions). Calls <see cref="InspectSwapRecoveryAsync"/> per
    /// candidate; runs a fresh arkd VTXO snapshot per swap so callers get
    /// a real-time view rather than a cached one.
    /// <para>
    /// <b>Cost:</b> this is an O(N) sequential operation — one arkd
    /// <c>GetVtxoByScriptsAsSnapshot</c> round-trip per non-pending swap
    /// candidate, plus one <c>GetSwaps</c> read each. Intended for
    /// recovery-audit flows after a wallet restore, not for hot-path UI.
    /// Wallets with many historical swaps should expect a few hundred ms
    /// per candidate against a local arkd; budget accordingly.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<SwapRecoveryInfo>> ScanRecoverableSwapsAsync(
        string walletId, CancellationToken ct = default)
    {
        var allSwaps = await _swapsStorage.GetSwaps(walletIds: [walletId], cancellationToken: ct);
        var candidates = allSwaps
            .Where(s => s.Status is not ArkSwapStatus.Pending)
            .ToList();

        var results = new List<SwapRecoveryInfo>(candidates.Count);
        foreach (var swap in candidates)
        {
            results.Add(await InspectSwapRecoveryAsync(walletId, swap.SwapId, ct));
        }
        return results;
    }

    // ─── Swap Restoration ──────────────────────────────────────────

    /// <summary>
    /// Restores swaps from Boltz for the given descriptors.
    /// Caller determines which descriptors to pass (current key, all used indexes, etc.)
    /// </summary>
    public async Task<IReadOnlyList<ArkSwap>> RestoreSwaps(
        string walletId,
        OutputDescriptor[] descriptors,
        CancellationToken cancellationToken = default)
    {
        if (descriptors.Length == 0)
            return [];

        var boltz = GetBoltzProvider();
        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);

        // Extract public keys from all descriptors
        var publicKeys = descriptors
            .Select(d => OutputDescriptorHelpers.Extract(d).PubKey?.ToBytes()?.ToHexStringLower())
            .Where(s => s is not null)
            .Select(s => s!)
            .Distinct()
            .ToArray();

        var restoredSwaps = await boltz.RestoreSwapsFromBoltzAsync(publicKeys, cancellationToken);
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
            Status: BoltzSwapStatus.ToArkSwapStatus(restored.Status),
            FailReason: null,
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(restored.CreatedAt),
            UpdatedAt: DateTimeOffset.UtcNow,
            Hash: restored.PreimageHash ?? ""
        )
        {
            ProviderId = BoltzSwapProvider.Id
        };
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
                sender = Extensions.KeyExtensions.ParseOutputDescriptor(details.ServerPublicKey, serverInfo.Network);
                receiver = FindMatchingDescriptor(descriptors, details) ?? descriptors[0];
            }
            else
            {
                // Submarine swap: we are the sender (refunding)
                sender = FindMatchingDescriptor(descriptors, details) ?? descriptors[0];
                receiver = Extensions.KeyExtensions.ParseOutputDescriptor(details.ServerPublicKey, serverInfo.Network);
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

    // ─── Enrichment Methods ────────────────────────────────────────

    /// <summary>
    /// Enriches a restored reverse swap with the preimage needed for claiming.
    /// Validates the preimage matches the stored hash before updating.
    /// </summary>
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

    // ─── Helpers ───────────────────────────────────────────────────

    private BoltzSwapProvider GetBoltzProvider()
    {
        return _boltzProvider
            ?? throw new InvalidOperationException("Boltz swap provider is not registered");
    }

    // ─── Disposal ──────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _logger?.LogInformation("Disposing swap management service");
        // Both events were subscribed in the ctor — both must come off, or
        // OnVtxosChanged would still fire on a disposed router and reach
        // providers that have themselves been disposed.
        _swapsStorage.SwapsChanged -= OnSwapsChanged;
        _vtxoStorage.VtxosChanged -= OnVtxosChanged;

        foreach (var provider in _providers)
        {
            try
            {
                await provider.DisposeAsync();
            }
            catch
            {
                // ignored
            }
        }
    }
}
