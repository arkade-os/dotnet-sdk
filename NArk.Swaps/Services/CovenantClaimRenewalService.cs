using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly ISwapStorage _swapStorage;
    private readonly IContractStorage _contractStorage;
    private readonly IClientTransport _clientTransport;
    private readonly ICovenantClaimProvider? _covenantClaimProvider;
    private readonly CovenantClaimRenewalOptions _options;
    private readonly ILogger<CovenantClaimRenewalService>? _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="swapStorage">Source of the swaps still waiting to be funded.</param>
    /// <param name="contractStorage">Used to reload each swap's stored VHTLC.</param>
    /// <param name="clientTransport">Supplies the server key and network for reconstruction.</param>
    /// <param name="covenantClaimProvider">Null when covenant claims are not configured.</param>
    /// <param name="options">Renewal cadence; defaults are used when not configured.</param>
    /// <param name="logger">Optional logger.</param>
    public CovenantClaimRenewalService(
        ISwapStorage swapStorage,
        IContractStorage contractStorage,
        IClientTransport clientTransport,
        ICovenantClaimProvider? covenantClaimProvider = null,
        IOptions<CovenantClaimRenewalOptions>? options = null,
        ILogger<CovenantClaimRenewalService>? logger = null)
    {
        _swapStorage = swapStorage;
        _contractStorage = contractStorage;
        _clientTransport = clientTransport;
        _covenantClaimProvider = covenantClaimProvider;
        _options = options?.Value ?? new CovenantClaimRenewalOptions();
        _logger = logger;
    }

    /// <summary>
    /// A configured fraction of the backend's advertised lifetime, or an explicit
    /// override, floored by <see cref="CovenantClaimRenewalOptions.MinimumInterval"/>.
    /// </summary>
    internal TimeSpan RenewalInterval
    {
        get
        {
            if (_options.Interval is { } fixedInterval)
                return Max(fixedInterval, _options.MinimumInterval);

            if (_covenantClaimProvider is null)
                return _options.MinimumInterval;

            var derived = _covenantClaimProvider.RegistrationLifetime * _options.RenewalFraction;
            return Max(derived, _options.MinimumInterval);
        }
    }

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

        // Renew immediately rather than after a full interval. On startup the signer may
        // have just restarted and dropped everything it was holding, and swaps created by
        // a previous run of this process have no live registration at all — waiting out
        // the first tick would leave them uncovered for no reason.
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!first)
                    await timer.WaitForNextTickAsync(cancellationToken);
                first = false;

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
        var isMainnet = serverInfo.Network.ChainName == ChainName.Mainnet;
        var renewed = 0;

        // One query for the whole pass rather than one per swap: a wallet with many
        // outstanding swaps would otherwise hit storage once per swap on every tick.
        var contracts = (await _contractStorage.GetContracts(
                walletIds: pending.Select(s => s.WalletId).Distinct().ToArray(),
                scripts: pending.Select(s => s.ContractScript).Distinct().ToArray(),
                cancellationToken: cancellationToken))
            .ToLookup(c => (c.WalletIdentifier, c.Script));

        foreach (var swap in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stored = contracts[(swap.WalletId, swap.ContractScript)].FirstOrDefault();
                if (stored is not null && await RenewAsync(swap, stored, serverInfo, isMainnet, cancellationToken))
                    renewed++;
            }
            catch (OperationCanceledException)
            {
                // Shutdown, not a renewal failure. Reported as one it would log a bogus
                // warning per pending swap on every clean stop and bury the real cause.
                throw;
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
        ArkSwap swap, ArkContractEntity stored, ArkServerInfo serverInfo, bool isMainnet,
        CancellationToken cancellationToken)
    {
        if (ArkContractParser.Parse(stored.Type, stored.AdditionalData, serverInfo.Network)
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
