using Microsoft.Extensions.Logging;
using NArk.Swaps.Abstractions;
using NArk.Swaps.LendaSwap.Client;
using NArk.Swaps.LendaSwap.Models;
using NArk.Swaps.Models;

namespace NArk.Swaps.LendaSwap;

/// <summary>
/// LendaSwap provider implementing ISwapProvider.
/// Supports cross-chain swaps between Bitcoin, Arkade, and EVM chains.
/// </summary>
public class LendaSwapProvider : ISwapProvider
{
    public const string Id = "lendaswap";

    private readonly LendaSwapClient _client;
    private readonly ISwapStorage _swapStorage;
    private readonly ILogger<LendaSwapProvider>? _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _pollingTask;

    public LendaSwapProvider(
        LendaSwapClient client,
        ISwapStorage swapStorage,
        ILogger<LendaSwapProvider>? logger = null)
    {
        _client = client;
        _swapStorage = swapStorage;
        _logger = logger;
    }

    public string ProviderId => Id;
    public string DisplayName => "LendaSwap";

    // ─── Route Support ─────────────────────────────────────────

    private static bool IsEvmNetwork(SwapNetwork n) =>
        n is SwapNetwork.EvmEthereum or SwapNetwork.EvmPolygon or SwapNetwork.EvmArbitrum;

    /// <inheritdoc />
    public bool SupportsRoute(SwapRoute route)
    {
        return route switch
        {
            // BTC Onchain <-> Ark
            { Source.Network: SwapNetwork.BitcoinOnchain, Destination.Network: SwapNetwork.Ark } => true,
            { Source.Network: SwapNetwork.Ark, Destination.Network: SwapNetwork.BitcoinOnchain } => true,

            // Lightning <-> Ark
            { Source.Network: SwapNetwork.Lightning, Destination.Network: SwapNetwork.Ark } => true,
            { Source.Network: SwapNetwork.Ark, Destination.Network: SwapNetwork.Lightning } => true,

            // Ark <-> EVM
            { Source.Network: SwapNetwork.Ark } when IsEvmNetwork(route.Destination.Network) => true,
            { Destination.Network: SwapNetwork.Ark } when IsEvmNetwork(route.Source.Network) => true,

            // Lightning <-> EVM
            { Source.Network: SwapNetwork.Lightning } when IsEvmNetwork(route.Destination.Network) => true,
            { Destination.Network: SwapNetwork.Lightning } when IsEvmNetwork(route.Source.Network) => true,

            // BTC Onchain <-> EVM
            { Source.Network: SwapNetwork.BitcoinOnchain } when IsEvmNetwork(route.Destination.Network) => true,
            { Destination.Network: SwapNetwork.BitcoinOnchain } when IsEvmNetwork(route.Source.Network) => true,

            _ => false
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct)
    {
        try
        {
            var tokens = await _client.GetTokensAsync(ct);
            if (tokens == null)
                return Array.Empty<SwapRoute>();

            var routes = new List<SwapRoute>();
            var hasBtc = tokens.BtcTokens.Exists(t => t.TokenId == "btc");

            if (hasBtc)
            {
                routes.Add(new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc));
                routes.Add(new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain));
                routes.Add(new SwapRoute(SwapAsset.BtcLightning, SwapAsset.ArkBtc));
                routes.Add(new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning));
            }

            foreach (var evmToken in tokens.EvmTokens)
            {
                var network = MapChainToNetwork(evmToken.Chain);
                if (network == null) continue;

                var evmAsset = SwapAsset.Erc20(network.Value, evmToken.TokenId);

                routes.Add(new SwapRoute(SwapAsset.ArkBtc, evmAsset));
                routes.Add(new SwapRoute(evmAsset, SwapAsset.ArkBtc));
                routes.Add(new SwapRoute(SwapAsset.BtcLightning, evmAsset));
                routes.Add(new SwapRoute(evmAsset, SwapAsset.BtcLightning));

                if (hasBtc)
                {
                    routes.Add(new SwapRoute(SwapAsset.BtcOnchain, evmAsset));
                    routes.Add(new SwapRoute(evmAsset, SwapAsset.BtcOnchain));
                }
            }

            return routes;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch LendaSwap tokens, returning empty routes");
            return Array.Empty<SwapRoute>();
        }
    }

    // ─── Lifecycle ─────────────────────────────────────────────

    public Task StartAsync(string walletId, CancellationToken ct)
    {
        _logger?.LogInformation("Starting LendaSwap provider");
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        _pollingTask = PollActiveSwaps(TimeSpan.FromSeconds(30), linkedCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _logger?.LogInformation("Stopping LendaSwap provider");
        return Task.CompletedTask;
    }

    // ─── Limits & Quotes ───────────────────────────────────────

    public async Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct)
    {
        var (sourceChain, sourceToken, targetChain, targetToken) = MapRouteToApiParams(route);

        var quote = await _client.GetQuoteAsync(
            sourceChain, sourceToken, targetChain, targetToken,
            sourceAmount: null, targetAmount: null, ct);

        if (quote == null)
            throw new InvalidOperationException($"Unable to fetch LendaSwap limits for route {route}");

        return new SwapLimits
        {
            Route = route,
            MinAmount = quote.MinAmount,
            MaxAmount = quote.MaxAmount,
            FeePercentage = quote.ProtocolFeeRate,
            MinerFee = quote.NetworkFee
        };
    }

    public async Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct)
    {
        var (sourceChain, sourceToken, targetChain, targetToken) = MapRouteToApiParams(route);

        var quote = await _client.GetQuoteAsync(
            sourceChain, sourceToken, targetChain, targetToken,
            sourceAmount: amount, ct: ct);

        if (quote == null)
            throw new InvalidOperationException($"Unable to fetch LendaSwap quote for route {route}");

        var exchangeRate = decimal.TryParse(quote.ExchangeRate, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var rate) ? rate : 0m;

        return new SwapQuote
        {
            Route = route,
            SourceAmount = quote.SourceAmount,
            DestinationAmount = quote.TargetAmount,
            TotalFees = quote.ProtocolFee + quote.NetworkFee,
            ExchangeRate = exchangeRate
        };
    }

    // ─── Status Events ─────────────────────────────────────────

    public event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;

    // ─── Polling ───────────────────────────────────────────────

    private async Task PollActiveSwaps(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var allActiveSwaps = await _swapStorage.GetSwaps(
                    active: true, cancellationToken: ct);
                var activeSwaps = allActiveSwaps.Where(s => s.ProviderId == Id);

                foreach (var swap in activeSwaps)
                {
                    try
                    {
                        var status = await _client.GetSwapStatusAsync(swap.SwapId, ct);
                        if (status?.Status == null) continue;

                        var newStatus = MapLendaSwapStatus(status.Status);
                        if (swap.Status == newStatus) continue;

                        _logger?.LogInformation(
                            "Swap {SwapId}: status changed {OldStatus} -> {NewStatus} (LendaSwap: '{ApiStatus}')",
                            swap.SwapId, swap.Status, newStatus, status.Status);

                        var updatedSwap = swap with
                        {
                            Status = newStatus,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };

                        await _swapStorage.SaveSwap(swap.WalletId, updatedSwap, cancellationToken: ct);

                        SwapStatusChanged?.Invoke(this, new SwapStatusChangedEvent(
                            swap.SwapId, swap.WalletId, Id, newStatus));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogError(ex, "Swap {SwapId}: error polling LendaSwap status", swap.SwapId);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Error during LendaSwap polling cycle");
            }

            await Task.Delay(interval, ct);
        }
    }

    // ─── Status Mapping ────────────────────────────────────────

    public static ArkSwapStatus MapLendaSwapStatus(string status)
    {
        return status switch
        {
            "pending" or "clientfundingseen" or "clientfunded" or "serverfunded"
                or "clientredeeming" => ArkSwapStatus.Pending,

            "clientredeemed" or "serverredeemed" => ArkSwapStatus.Settled,

            "clientrefunded" or "clientrefundedserverfunded"
                or "clientrefundedserverrefunded" => ArkSwapStatus.Refunded,

            "expired" or "clientinvalidfunded" or "clientfundedtoolate"
                or "clientfundedserverrefunded"
                or "clientredeemedandclientrefunded" => ArkSwapStatus.Failed,

            _ => ArkSwapStatus.Unknown
        };
    }

    // ─── Chain / Network Mapping ───────────────────────────────

    public static SwapNetwork? MapChainToNetwork(string chain)
    {
        return chain switch
        {
            "Arkade" => SwapNetwork.Ark,
            "Bitcoin" => SwapNetwork.BitcoinOnchain,
            "Lightning" => SwapNetwork.Lightning,
            "1" => SwapNetwork.EvmEthereum,
            "137" => SwapNetwork.EvmPolygon,
            "42161" => SwapNetwork.EvmArbitrum,
            _ => null
        };
    }

    public static string MapNetworkToChain(SwapNetwork network)
    {
        return network switch
        {
            SwapNetwork.Ark => "Arkade",
            SwapNetwork.BitcoinOnchain => "Bitcoin",
            SwapNetwork.Lightning => "Lightning",
            SwapNetwork.EvmEthereum => "1",
            SwapNetwork.EvmPolygon => "137",
            SwapNetwork.EvmArbitrum => "42161",
            _ => throw new ArgumentOutOfRangeException(nameof(network), network, "Unsupported network")
        };
    }

    public static (string sourceChain, string sourceToken, string targetChain, string targetToken)
        MapRouteToApiParams(SwapRoute route)
    {
        var sourceChain = MapNetworkToChain(route.Source.Network);
        var targetChain = MapNetworkToChain(route.Destination.Network);

        // For BTC/Ark networks, the token is always "btc"
        // For EVM networks, the token is the contract address (AssetId)
        var sourceToken = route.Source.Network is SwapNetwork.Ark or SwapNetwork.BitcoinOnchain or SwapNetwork.Lightning
            ? "btc"
            : route.Source.AssetId;

        var targetToken = route.Destination.Network is SwapNetwork.Ark or SwapNetwork.BitcoinOnchain or SwapNetwork.Lightning
            ? "btc"
            : route.Destination.AssetId;

        return (sourceChain, sourceToken, targetChain, targetToken);
    }

    // ─── Disposal ──────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _logger?.LogInformation("Disposing LendaSwap provider");

        await _shutdownCts.CancelAsync();

        try
        {
            if (_pollingTask is not null)
                await _pollingTask;
        }
        catch
        {
            // ignored — task cancellation is expected
        }
    }
}
