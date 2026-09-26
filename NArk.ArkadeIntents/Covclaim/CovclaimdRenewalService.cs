using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.Core.Transport;

namespace NArk.ArkadeIntents.Covclaim;

/// <summary>Re-reveals every live receive to covclaimd before the daemon forgets it.</summary>
/// <remarks>
/// A registration disappears when its TTL lapses or the daemon restarts, and nothing announces
/// either — the wallet's own claimer carries on as before. So this re-asserts every live receive
/// rather than tracking expiry, at half the TTL, which covers one missed pass without a gap.
/// </remarks>
public sealed class CovclaimdRenewalService : BackgroundService
{
    private readonly ICovclaimdClient _covclaimd;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly IContractStorage _contractStorage;
    private readonly IClientTransport _transport;
    private readonly TimeSpan _interval;
    private readonly ILogger<CovclaimdRenewalService>? _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="covclaimd">The daemon to re-reveal to.</param>
    /// <param name="intentStorage">Where the live swaps are found.</param>
    /// <param name="contractStorage">Where each lockup's covenant is read back from.</param>
    /// <param name="transport">Supplies the network a stored covenant is rebuilt against.</param>
    /// <param name="options">Supplies the TTL this paces itself against.</param>
    /// <param name="logger">Optional log sink.</param>
    public CovclaimdRenewalService(
        ICovclaimdClient covclaimd,
        IArkadeIntentStorage intentStorage,
        IContractStorage contractStorage,
        IClientTransport transport,
        IOptions<CovclaimdOptions>? options = null,
        ILogger<CovclaimdRenewalService>? logger = null)
    {
        _covclaimd = covclaimd;
        _intentStorage = intentStorage;
        _contractStorage = contractStorage;
        _transport = transport;
        _logger = logger;

        var ttl = (options?.Value ?? new CovclaimdOptions()).RegistrationTtl;
        _interval = ttl > TimeSpan.Zero ? ttl / 2 : TimeSpan.FromMinutes(7.5);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RenewAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A pass that threw must not end the loop that would have retried it.
                _logger?.LogError(ex, "a covclaimd renewal pass failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Re-reveal every receive that can still be claimed.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many registrations the daemon accepted.</returns>
    /// <remarks>Public so a host without a background loop can drive the pass on its own schedule.</remarks>
    public async Task<int> RenewAsync(CancellationToken cancellationToken = default)
    {
        var live = await _intentStorage.GetArkadeSwapIntents(
            statuses: [ArkadeSwapIntentStatus.Pending, ArkadeSwapIntentStatus.Claimable],
            cancellationToken: cancellationToken);

        var receives = live
            .Where(i => i.Type is ArkadeSwapIntentType.LightningToBtc or ArkadeSwapIntentType.OnchainToBtc)
            .ToList();
        if (receives.Count == 0)
        {
            return 0;
        }

        var network = (await _transport.GetServerInfoAsync(cancellationToken)).Network;
        var renewed = 0;

        foreach (var intent in receives)
        {
            if (intent.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.Preimage) is not { Length: > 0 } hex)
            {
                continue;
            }

            VHTLCv2Contract contract;
            try
            {
                contract = await LightningCorridor.LoadLockupAsync(
                    _contractStorage, intent.SwapPkScript, intent.Id, network, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex, "could not rebuild the lockup for swap {SwapId}; skipping its renewal", intent.Id);
                continue;
            }

            if (await CovclaimdRegistration.TryRegisterAsync(
                    _covclaimd, contract, intent.SwapAddress, Convert.FromHexString(hex),
                    _logger, cancellationToken))
            {
                renewed++;
            }
        }

        if (renewed > 0)
        {
            _logger?.LogDebug("re-revealed {Count} live receives to covclaimd", renewed);
        }
        return renewed;
    }
}
