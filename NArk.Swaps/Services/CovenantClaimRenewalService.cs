using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NBitcoin;

namespace NArk.Swaps.Services;

/// <summary>
/// Keeps covenant claim authorisations alive for swaps that have not been funded yet.
/// </summary>
/// <remarks>
/// <para>
/// A claim signer holds an authorisation only briefly — covclaimd's registry is
/// in-memory with a 15 minute TTL and is lost entirely when the daemon restarts. The
/// initial registration happens when the swap is created, but the signer is needed when
/// the swap is <em>funded</em>, and nothing bounds the gap between the two: a Lightning
/// payer may take hours to pay an invoice, and a chain swap counterparty sends BTC
/// whenever they please. Without renewal the offline safety net quietly lapses long
/// before the swap it was meant to protect.
/// </para>
/// <para>
/// Registering again is safe: the signer replaces the entry with an equivalent one, and
/// entries for swaps that were already claimed are dropped on its side.
/// </para>
/// <para>
/// Does nothing unless an <see cref="ICovenantClaimProvider"/> is registered.
/// </para>
/// </remarks>
public class CovenantClaimRenewalService : IHostedService, IAsyncDisposable
{
    /// <summary>Never poll faster than this, however short the signer's TTL is.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly ISwapStorage _swapStorage;
    private readonly IContractStorage _contractStorage;
    private readonly IClientTransport _clientTransport;
    private readonly ICovenantClaimProvider? _covenantClaimProvider;
    private readonly ILogger<CovenantClaimRenewalService>? _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="swapStorage">Source of the swaps still waiting to be funded.</param>
    /// <param name="contractStorage">Used to reload each swap's stored VHTLC.</param>
    /// <param name="clientTransport">Supplies the server key and network for reconstruction.</param>
    /// <param name="covenantClaimProvider">Null when covenant claims are not configured.</param>
    /// <param name="logger">Optional logger.</param>
    public CovenantClaimRenewalService(
        ISwapStorage swapStorage,
        IContractStorage contractStorage,
        IClientTransport clientTransport,
        ICovenantClaimProvider? covenantClaimProvider = null,
        ILogger<CovenantClaimRenewalService>? logger = null)
    {
        _swapStorage = swapStorage;
        _contractStorage = contractStorage;
        _clientTransport = clientTransport;
        _covenantClaimProvider = covenantClaimProvider;
        _logger = logger;
    }

    /// <summary>
    /// Half the signer's advertised lifetime, so a single missed pass still leaves the
    /// authorisation valid.
    /// </summary>
    internal TimeSpan RenewalInterval =>
        _covenantClaimProvider is null
            ? MinimumInterval
            : Max(_covenantClaimProvider.RegistrationLifetime / 2, MinimumInterval);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_covenantClaimProvider is null)
        {
            _logger?.LogDebug(
                "No covenant claim provider registered; skipping claim renewal");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);

        _logger?.LogInformation(
            "Renewing covenant claims every {Interval}", RenewalInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
            return;

        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected: either our own cancellation or the caller giving up waiting.
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RenewalInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
                await RenewAllAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A renewal pass is best-effort upkeep; losing one must not take the
                // loop down and silently end coverage for every pending swap.
                _logger?.LogWarning(ex, "Covenant claim renewal pass failed");
            }
        }
    }

    /// <summary>
    /// Re-registers every unfunded swap that carries a covenant claim leaf.
    /// </summary>
    internal async Task RenewAllAsync(CancellationToken cancellationToken)
    {
        if (_covenantClaimProvider is null)
            return;

        var pending = await _swapStorage.GetSwaps(
            active: true, status: [ArkSwapStatus.Pending], cancellationToken: cancellationToken);
        if (pending.Count == 0)
            return;

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        var isMainnet = serverInfo.Network == Network.Main;
        var renewed = 0;

        foreach (var swap in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await RenewAsync(swap, serverInfo, isMainnet, cancellationToken))
                    renewed++;
            }
            catch (Exception ex)
            {
                // One unhappy swap must not stop the others from being renewed.
                _logger?.LogWarning(ex,
                    "Could not renew covenant claim for swap {SwapId}", swap.SwapId);
            }
        }

        if (renewed > 0)
            _logger?.LogDebug("Renewed {Count} covenant claim(s)", renewed);
    }

    /// <summary>
    /// Renews a single swap, or returns false when it has no covenant claim to renew.
    /// </summary>
    private async Task<bool> RenewAsync(
        ArkSwap swap, ArkServerInfo serverInfo, bool isMainnet, CancellationToken cancellationToken)
    {
        var stored = (await _contractStorage.GetContracts(
                walletIds: [swap.WalletId],
                scripts: [swap.ContractScript],
                cancellationToken: cancellationToken))
            .FirstOrDefault();

        if (stored is null ||
            ArkContractParser.Parse(stored.Type, stored.AdditionalData, serverInfo.Network)
                is not VHTLCContract contract)
            return false;

        // No leaf means nothing to authorise; no preimage means we could not tell the
        // signer how to unlock it even if there were.
        if (contract.CovenantClaimKey is null || contract.Preimage is null)
            return false;

        // Rebuilt rather than stored: the destination is a pure function of the VHTLC's
        // receiver descriptor, and deriving it keeps it identical to the one the initial
        // registration used and the one a normal sweep would recycle into.
        var claimDestination = new ArkPaymentContract(
            serverInfo.SignerKey, serverInfo.UnilateralExit, contract.Receiver).GetArkAddress();

        await _covenantClaimProvider!.RegisterAsync(
            contract.GetArkAddress().ToString(isMainnet),
            contract.Preimage,
            claimDestination.ScriptPubKey,
            contract.GetTapScriptList(),
            cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
