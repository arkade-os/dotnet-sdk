# Multi-Provider Swap Architecture — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make the NArk.Swaps package multi-provider with pluggable DI, add LendaSwap as the second provider alongside Boltz.

**Architecture:** Provider interface (`ISwapProvider`) with capability-based route discovery (`SwapRoute(SwapAsset, SwapAsset)`). `SwapsManagementService` becomes a router that delegates to providers. Boltz logic extracted into `BoltzSwapProvider`. New `LendaSwapProvider` wraps LendaSwap's REST API.

**Tech Stack:** .NET 8, NUnit, NSubstitute (mocking), `System.Net.Http.Json`, existing NArk.Core/NArk.Swaps packages.

**Design doc:** `docs/plans/2026-02-27-multi-provider-swaps-design.md`

---

## Phase 1: Core Abstractions

### Task 1: Create SwapNetwork, SwapAsset, SwapRoute types

**Files:**
- Create: `NArk.Swaps/Abstractions/SwapNetwork.cs`
- Create: `NArk.Swaps/Abstractions/SwapAsset.cs`
- Create: `NArk.Swaps/Abstractions/SwapRoute.cs`
- Test: `NArk.Tests/SwapRouteTests.cs`

**Step 1: Write the failing tests**

```csharp
// NArk.Tests/SwapRouteTests.cs
using NArk.Swaps.Abstractions;

namespace NArk.Tests;

[TestFixture]
public class SwapRouteTests
{
    [Test]
    public void WellKnownAssets_HaveCorrectNetworkAndId()
    {
        Assert.That(SwapAsset.BtcOnchain.Network, Is.EqualTo(SwapNetwork.BitcoinOnchain));
        Assert.That(SwapAsset.BtcOnchain.AssetId, Is.EqualTo("BTC"));
        Assert.That(SwapAsset.BtcLightning.Network, Is.EqualTo(SwapNetwork.Lightning));
        Assert.That(SwapAsset.ArkBtc.Network, Is.EqualTo(SwapNetwork.Ark));
    }

    [Test]
    public void Erc20Factory_CreatesAssetWithContractAddress()
    {
        var usdc = SwapAsset.Erc20(SwapNetwork.EvmPolygon, "0x3c499c542cEF5E3811e1192ce70d8cC03d5c3359");
        Assert.That(usdc.Network, Is.EqualTo(SwapNetwork.EvmPolygon));
        Assert.That(usdc.AssetId, Is.EqualTo("0x3c499c542cEF5E3811e1192ce70d8cC03d5c3359"));
    }

    [Test]
    public void SwapRoute_EqualityByValue()
    {
        var route1 = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain);
        var route2 = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain);
        Assert.That(route1, Is.EqualTo(route2));
    }

    [Test]
    public void SwapRoute_DifferentDirections_AreNotEqual()
    {
        var forward = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain);
        var reverse = new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc);
        Assert.That(forward, Is.Not.EqualTo(reverse));
    }

    [Test]
    public void SwapRoute_WorksInHashSet()
    {
        var set = new HashSet<SwapRoute>
        {
            new(SwapAsset.ArkBtc, SwapAsset.BtcOnchain),
            new(SwapAsset.ArkBtc, SwapAsset.BtcOnchain), // duplicate
            new(SwapAsset.BtcOnchain, SwapAsset.ArkBtc),
        };
        Assert.That(set, Has.Count.EqualTo(2));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test NArk.Tests/ --filter "FullyQualifiedName~SwapRouteTests" -v n`
Expected: Build error — types don't exist yet.

**Step 3: Write the implementations**

```csharp
// NArk.Swaps/Abstractions/SwapNetwork.cs
namespace NArk.Swaps.Abstractions;

public enum SwapNetwork
{
    Ark,
    BitcoinOnchain,
    Lightning,
    EvmEthereum,
    EvmPolygon,
    EvmArbitrum
}
```

```csharp
// NArk.Swaps/Abstractions/SwapAsset.cs
namespace NArk.Swaps.Abstractions;

public record SwapAsset(SwapNetwork Network, string AssetId)
{
    public static readonly SwapAsset BtcOnchain = new(SwapNetwork.BitcoinOnchain, "BTC");
    public static readonly SwapAsset BtcLightning = new(SwapNetwork.Lightning, "BTC");
    public static readonly SwapAsset ArkBtc = new(SwapNetwork.Ark, "BTC");

    public static SwapAsset Erc20(SwapNetwork chain, string contractAddress)
        => new(chain, contractAddress);

    public static SwapAsset ArkAsset(string assetId)
        => new(SwapNetwork.Ark, assetId);
}
```

```csharp
// NArk.Swaps/Abstractions/SwapRoute.cs
namespace NArk.Swaps.Abstractions;

public record SwapRoute(SwapAsset Source, SwapAsset Destination);
```

**Step 4: Run test to verify it passes**

Run: `dotnet test NArk.Tests/ --filter "FullyQualifiedName~SwapRouteTests" -v n`
Expected: All 5 tests PASS.

**Step 5: Commit**

```bash
git add NArk.Swaps/Abstractions/SwapNetwork.cs NArk.Swaps/Abstractions/SwapAsset.cs NArk.Swaps/Abstractions/SwapRoute.cs NArk.Tests/SwapRouteTests.cs
git commit -m "feat: add SwapNetwork, SwapAsset, SwapRoute core types"
```

---

### Task 2: Create ISwapProvider interface and event types

**Files:**
- Create: `NArk.Swaps/Abstractions/ISwapProvider.cs`
- Create: `NArk.Swaps/Abstractions/SwapStatusChangedEvent.cs`

No tests for interfaces — they're contracts, not behavior.

**Step 1: Create the interface**

```csharp
// NArk.Swaps/Abstractions/ISwapProvider.cs
namespace NArk.Swaps.Abstractions;

public interface ISwapProvider : IAsyncDisposable
{
    string ProviderId { get; }
    string DisplayName { get; }

    bool SupportsRoute(SwapRoute route);
    Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct);

    Task StartAsync(string walletId, CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    Task<SwapResult> CreateSwapAsync(CreateSwapRequest request, CancellationToken ct);
    Task RefundSwapAsync(string walletId, string swapId, CancellationToken ct);

    Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct);
    Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct);

    event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;
}
```

```csharp
// NArk.Swaps/Abstractions/SwapStatusChangedEvent.cs
using NArk.Swaps.Models;

namespace NArk.Swaps.Abstractions;

public record SwapStatusChangedEvent(
    string SwapId,
    string WalletId,
    string ProviderId,
    ArkSwapStatus NewStatus,
    string? FailReason = null);
```

**Step 2: Verify it compiles**

Run: `dotnet build NArk.Swaps/`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add NArk.Swaps/Abstractions/ISwapProvider.cs NArk.Swaps/Abstractions/SwapStatusChangedEvent.cs
git commit -m "feat: add ISwapProvider interface and SwapStatusChangedEvent"
```

---

### Task 3: Create request/response type hierarchy

**Files:**
- Create: `NArk.Swaps/Abstractions/CreateSwapRequest.cs`
- Create: `NArk.Swaps/Abstractions/SwapResult.cs`
- Create: `NArk.Swaps/Abstractions/SwapLimits.cs`
- Create: `NArk.Swaps/Abstractions/SwapQuote.cs`

**Step 1: Create the types**

```csharp
// NArk.Swaps/Abstractions/CreateSwapRequest.cs
namespace NArk.Swaps.Abstractions;

public abstract record CreateSwapRequest
{
    public required string WalletId { get; init; }
    public required SwapRoute Route { get; init; }
    public required long Amount { get; init; }
    public string? PreferredProviderId { get; init; }
}

public record LightningSwapRequest : CreateSwapRequest
{
    public required string Invoice { get; init; }
}

public record EvmSwapRequest : CreateSwapRequest
{
    public required string EvmAddress { get; init; }
    public required string TokenContract { get; init; }
}

public record OnchainSwapRequest : CreateSwapRequest
{
    public string? DestinationAddress { get; init; }
}

public record SimpleSwapRequest : CreateSwapRequest;
```

```csharp
// NArk.Swaps/Abstractions/SwapResult.cs
using NArk.Swaps.Models;

namespace NArk.Swaps.Abstractions;

public abstract record SwapResult
{
    public required string SwapId { get; init; }
    public required string ProviderId { get; init; }
    public required SwapRoute Route { get; init; }
    public required long Amount { get; init; }
    public required ArkSwapStatus Status { get; init; }
    public DateTimeOffset? Expiry { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public record DepositSwapResult : SwapResult
{
    public required string DepositAddress { get; init; }
}

public record InvoiceSwapResult : SwapResult
{
    public required string Invoice { get; init; }
}

public record VhtlcSwapResult : SwapResult
{
    public required string ContractScript { get; init; }
    public required string ContractAddress { get; init; }
}
```

```csharp
// NArk.Swaps/Abstractions/SwapLimits.cs
namespace NArk.Swaps.Abstractions;

public record SwapLimits
{
    public required SwapRoute Route { get; init; }
    public required long MinAmount { get; init; }
    public required long MaxAmount { get; init; }
    public required decimal FeePercentage { get; init; }
    public required long MinerFee { get; init; }
}
```

```csharp
// NArk.Swaps/Abstractions/SwapQuote.cs
namespace NArk.Swaps.Abstractions;

public record SwapQuote
{
    public required SwapRoute Route { get; init; }
    public required long SourceAmount { get; init; }
    public required long DestinationAmount { get; init; }
    public required long TotalFees { get; init; }
    public required decimal ExchangeRate { get; init; }
    public DateTimeOffset? ValidUntil { get; init; }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build NArk.Swaps/`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add NArk.Swaps/Abstractions/CreateSwapRequest.cs NArk.Swaps/Abstractions/SwapResult.cs NArk.Swaps/Abstractions/SwapLimits.cs NArk.Swaps/Abstractions/SwapQuote.cs
git commit -m "feat: add request/response type hierarchy for multi-provider swaps"
```

---

## Phase 2: Extend ArkSwap Model

### Task 4: Add Route and ProviderId to ArkSwap

**Files:**
- Modify: `NArk.Swaps/Models/ArkSwap.cs`

**Step 1: Add new fields to ArkSwap record**

Add to the `ArkSwap` record body (alongside existing `Metadata` property):

```csharp
public SwapRoute? Route { get; init; }
public string? ProviderId { get; init; }
```

Add `using NArk.Swaps.Abstractions;` to the top.

**Step 2: Verify it compiles and existing tests pass**

Run: `dotnet build NArk.Swaps/ && dotnet test NArk.Tests/ -v n`
Expected: Build succeeds. All existing tests pass (nullable new fields = backward compatible).

**Step 3: Commit**

```bash
git add NArk.Swaps/Models/ArkSwap.cs
git commit -m "feat: add Route and ProviderId to ArkSwap model"
```

---

## Phase 3: Extract BoltzSwapProvider

This is the largest and most delicate phase. We're extracting Boltz-specific logic from `SwapsManagementService` into a new `BoltzSwapProvider` class.

### Task 5: Create BoltzSwapProvider skeleton implementing ISwapProvider

**Files:**
- Create: `NArk.Swaps/Boltz/BoltzSwapProvider.cs`

**Step 1: Create provider with route declarations and constructor**

The provider wraps all existing Boltz dependencies. Its constructor takes the same Boltz-specific deps that `SwapsManagementService` currently holds.

```csharp
// NArk.Swaps/Boltz/BoltzSwapProvider.cs
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
// ... other necessary usings from SwapsManagementService

namespace NArk.Swaps.Boltz;

public class BoltzSwapProvider : ISwapProvider
{
    public string ProviderId => "boltz";
    public string DisplayName => "Boltz";

    private static readonly HashSet<SwapRoute> _routes = new()
    {
        new(SwapAsset.ArkBtc, SwapAsset.BtcLightning),    // Submarine
        new(SwapAsset.BtcLightning, SwapAsset.ArkBtc),    // Reverse
        new(SwapAsset.BtcOnchain, SwapAsset.ArkBtc),      // Chain BTC→ARK
        new(SwapAsset.ArkBtc, SwapAsset.BtcOnchain),      // Chain ARK→BTC
    };

    private readonly BoltzSwapService _boltzService;
    private readonly CachedBoltzClient _cachedClient;
    private readonly BoltzClient _boltzClient;
    private readonly BoltzLimitsValidator _limitsValidator;
    private readonly ChainSwapMusigSession _chainSwapMusig;
    // ... other deps migrated from SwapsManagementService

    public BoltzSwapProvider(
        BoltzClient boltzClient,
        CachedBoltzClient cachedClient,
        BoltzLimitsValidator limitsValidator,
        IClientTransport clientTransport,
        // ... remaining deps that SwapsManagementService currently uses for Boltz ops
        ILogger<BoltzSwapProvider>? logger = null)
    {
        _boltzClient = boltzClient;
        _cachedClient = cachedClient;
        _limitsValidator = limitsValidator;
        _boltzService = new BoltzSwapService(boltzClient, clientTransport);
        _chainSwapMusig = new ChainSwapMusigSession(boltzClient);
        // ...
    }

    public bool SupportsRoute(SwapRoute route) => _routes.Contains(route);

    public Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<SwapRoute>>(_routes.ToList().AsReadOnly());

    // Lifecycle, swap operations, limits, quotes — implemented by migrating
    // the corresponding methods from SwapsManagementService
    // (detailed in Tasks 6-7)

    public event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;

    public Task StartAsync(string walletId, CancellationToken ct)
    {
        // Migrate status polling + WebSocket subscription from SwapsManagementService
        throw new NotImplementedException();
    }

    public Task StopAsync(CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<SwapResult> CreateSwapAsync(CreateSwapRequest request, CancellationToken ct)
    {
        // Route to correct Boltz method based on request.Route
        throw new NotImplementedException();
    }

    public Task RefundSwapAsync(string walletId, string swapId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build NArk.Swaps/`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add NArk.Swaps/Boltz/BoltzSwapProvider.cs
git commit -m "feat: add BoltzSwapProvider skeleton implementing ISwapProvider"
```

---

### Task 6: Migrate Boltz swap creation methods into BoltzSwapProvider

**Files:**
- Modify: `NArk.Swaps/Boltz/BoltzSwapProvider.cs`
- Reference: `NArk.Swaps/Services/SwapsManagementService.cs` (lines 532-790)

**Step 1: Implement CreateSwapAsync by routing to existing BoltzSwapService methods**

The `CreateSwapAsync` method inspects `request.Route` and delegates to the appropriate `BoltzSwapService` method. Each swap type returns a typed `SwapResult` subclass.

This is a **move, not a rewrite**. Copy the existing logic from `SwapsManagementService.InitiateSubmarineSwap()`, `InitiateReverseSwap()`, etc. into methods inside `BoltzSwapProvider`, adapting the signatures to use the new `CreateSwapRequest`/`SwapResult` types.

Key mapping:
- `Ark→Lightning` → calls `_boltzService.CreateSubmarineSwap()` → returns `VhtlcSwapResult`
- `Lightning→Ark` → calls `_boltzService.CreateReverseSwap()` → returns `InvoiceSwapResult`
- `BTC→Ark` → calls `_boltzService.CreateBtcToArkSwapAsync()` → returns `DepositSwapResult`
- `Ark→BTC` → calls `_boltzService.CreateArkToBtcSwapAsync()` → returns `VhtlcSwapResult`

**Important:** The provider does NOT call `SpendingService.Spend()` or `ISwapStorage.SaveSwap()` — those stay in the router (`SwapsManagementService`). The provider only creates the swap with the external service and returns the result.

**Step 2: Verify it compiles**

Run: `dotnet build NArk.Swaps/`

**Step 3: Commit**

```bash
git add NArk.Swaps/Boltz/BoltzSwapProvider.cs
git commit -m "feat: migrate Boltz swap creation into BoltzSwapProvider"
```

---

### Task 7: Migrate Boltz status polling, WebSocket, claiming, and refund into BoltzSwapProvider

**Files:**
- Modify: `NArk.Swaps/Boltz/BoltzSwapProvider.cs`
- Reference: `NArk.Swaps/Services/SwapsManagementService.cs` (lines 217-528, 796-986)

**Step 1: Migrate lifecycle and status monitoring**

Move from `SwapsManagementService`:
- `StartAsync` polling loop (lines 128-138, 217-305) → `BoltzSwapProvider.StartAsync()`
- WebSocket subscription (lines 464-528) → internal to provider
- Status mapping logic (lines 437-453) → internal to provider
- `TryClaimBtcForChainSwap` (lines 796-912) → internal claiming
- `TrySignBoltzBtcClaim` (lines 920-986) → internal cross-signing
- `RequestRefundCooperatively` (lines 307-435) → `RefundSwapAsync()`

When status changes, provider fires `SwapStatusChanged` event instead of directly updating storage.

**Step 2: Implement GetLimitsAsync and GetQuoteAsync**

Wrap existing `BoltzLimitsValidator` methods:
- `GetLimitsAsync` → delegates to `_limitsValidator.GetLimitsAsync()` or `GetChainLimitsAsync()` based on route
- `GetQuoteAsync` → fetches pairs from `_cachedClient` and computes quote

**Step 3: Verify it compiles**

Run: `dotnet build NArk.Swaps/`

**Step 4: Commit**

```bash
git add NArk.Swaps/Boltz/BoltzSwapProvider.cs
git commit -m "feat: migrate Boltz status polling and claiming into BoltzSwapProvider"
```

---

### Task 8: Refactor SwapsManagementService into router

**Files:**
- Modify: `NArk.Swaps/Services/SwapsManagementService.cs`

**Step 1: Change constructor to accept IEnumerable\<ISwapProvider\>**

Replace individual Boltz dependencies with `IEnumerable<ISwapProvider> providers`. Keep shared dependencies: `ISwapStorage`, `IVtxoStorage`, `IWalletProvider`, `IContractService`, `IContractStorage`, `ISafetyService`, `SpendingService`, `IClientTransport`, `IChainTimeProvider`.

**Step 2: Add provider resolution**

```csharp
private ISwapProvider ResolveProvider(SwapRoute route, string? preferredProviderId)
{
    var candidates = _providers.Where(p => p.SupportsRoute(route)).ToList();
    if (candidates.Count == 0)
        throw new InvalidOperationException($"No provider supports route {route}");

    if (preferredProviderId != null)
    {
        var preferred = candidates.FirstOrDefault(p => p.ProviderId == preferredProviderId);
        if (preferred != null) return preferred;
    }

    return candidates[0]; // First registered provider wins
}
```

**Step 3: Add aggregated capability methods**

```csharp
public IReadOnlySet<SwapRoute> GetAvailableRoutes()
    => _providers.SelectMany(p =>
    {
        try { return p.GetAvailableRoutesAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch { return []; }
    }).ToHashSet();

public IEnumerable<string> GetProvidersForRoute(SwapRoute route)
    => _providers.Where(p => p.SupportsRoute(route)).Select(p => p.ProviderId);
```

**Step 4: Convert InitiateSwapAsync to use router**

New generic entry point that replaces the 4 separate `Initiate*Swap` methods:

```csharp
public async Task<SwapResult> InitiateSwapAsync(CreateSwapRequest request, CancellationToken ct)
{
    var provider = ResolveProvider(request.Route, request.PreferredProviderId);
    var result = await provider.CreateSwapAsync(request, ct);

    // Save to storage (shared concern)
    var arkSwap = MapToArkSwap(result, request.WalletId);
    await _swapsStorage.SaveSwap(request.WalletId, arkSwap, ct);

    return result;
}
```

**Step 5: Keep existing `Initiate*Swap` methods as wrappers for backward compat**

The existing public API (`InitiateSubmarineSwap`, `InitiateReverseSwap`, etc.) delegates to `InitiateSwapAsync` with appropriate request mapping. Mark them `[Obsolete]` with message to use `InitiateSwapAsync`.

**Step 6: Wire up provider status change events**

In `StartAsync`, subscribe to each provider's `SwapStatusChanged` event and update `ISwapStorage` accordingly.

**Step 7: Run existing E2E tests**

Run: `dotnet test NArk.Tests.End2End/ -v n`
Expected: All existing tests pass — this is a refactoring, not a behavioral change.

**Step 8: Commit**

```bash
git add NArk.Swaps/Services/SwapsManagementService.cs
git commit -m "refactor: convert SwapsManagementService to multi-provider router"
```

---

### Task 9: Update DI registration for provider pattern

**Files:**
- Modify: `NArk.Swaps/Hosting/SwapServiceCollectionExtensions.cs`
- Modify: `NArk.Swaps/Hosting/SwapApplicationBuilderExtensions.cs`
- Modify: `NArk.Swaps/Hosting/SwapHostedLifecycle.cs`

**Step 1: Split AddArkSwapServices into core + provider registration**

```csharp
// Core registration (provider-agnostic)
public static IServiceCollection AddArkSwapServices(this IServiceCollection services)
{
    services.AddSingleton<SwapsManagementService>();
    services.AddSingleton<ISweepPolicy, SwapSweepPolicy>();
    services.AddSingleton<IContractTransformer, VHTLCContractTransformer>();
    services.AddHostedService<SwapHostedLifecycle>();
    return services;
}

// Boltz provider registration
public static IServiceCollection AddBoltzProvider(
    this IServiceCollection services,
    Action<BoltzClientOptions>? configure = null)
{
    if (configure != null)
        services.Configure(configure);

    services.AddSingleton<BoltzClient>();
    services.AddSingleton<CachedBoltzClient>();
    services.AddSingleton<BoltzLimitsValidator>();
    services.AddSingleton<ISwapProvider, BoltzSwapProvider>();
    services.AddHttpClient<BoltzClient>();

    // Auto-configure from ArkNetworkConfig if available
    services.AddOptions<BoltzClientOptions>()
        .Configure<ArkNetworkConfig>((boltz, config) =>
        {
            if (!string.IsNullOrWhiteSpace(config.BoltzUri))
            {
                boltz.BoltzUrl ??= config.BoltzUri;
                boltz.WebsocketUrl ??= config.BoltzUri;
            }
        });

    return services;
}
```

**Step 2: Update builder extensions**

```csharp
public static ArkClientBuilder WithBoltz(this ArkClientBuilder builder, string url, string wsUrl)
{
    builder.Services.AddBoltzProvider(opts =>
    {
        opts.BoltzUrl = url;
        opts.WebsocketUrl = wsUrl;
    });
    return builder;
}

public static ArkClientBuilder WithLendaSwap(this ArkClientBuilder builder, string apiUrl, string? apiKey = null)
{
    builder.Services.AddLendaSwapProvider(opts =>
    {
        opts.ApiUrl = apiUrl;
        opts.ApiKey = apiKey;
    });
    return builder;
}
```

**Step 3: Verify build and existing tests**

Run: `dotnet build NArk.Swaps/ && dotnet test NArk.Tests/ -v n`

**Step 4: Commit**

```bash
git add NArk.Swaps/Hosting/
git commit -m "refactor: split DI registration into core + per-provider extensions"
```

---

### Task 10: Run full E2E test suite as regression check

**Step 1: Run all E2E tests**

Run: `dotnet test NArk.Tests.End2End/ -v n`
Expected: All existing Boltz swap tests pass. This verifies the refactoring didn't break anything.

**Step 2: If any test fails, fix the regression before proceeding**

Do NOT proceed to Phase 4 until all existing tests pass.

**Step 3: Commit any fixes**

```bash
git add -A
git commit -m "fix: resolve regressions from provider extraction"
```

---

## Phase 4: LendaSwap Client

### Task 11: Create LendaSwap configuration and client base

**Files:**
- Create: `NArk.Swaps/LendaSwap/LendaSwapOptions.cs`
- Create: `NArk.Swaps/LendaSwap/Client/LendaSwapClient.cs`
- Test: `NArk.Tests/LendaSwapClientTests.cs`

**Step 1: Write failing test for client construction and token fetching**

```csharp
// NArk.Tests/LendaSwapClientTests.cs
using System.Net;
using System.Text.Json;
using NArk.Swaps.LendaSwap;
using NArk.Swaps.LendaSwap.Client;
using NArk.Swaps.LendaSwap.Models;
using Microsoft.Extensions.Options;

namespace NArk.Tests;

[TestFixture]
public class LendaSwapClientTests
{
    private LendaSwapClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.test.lendaswap.com") };
        var options = Options.Create(new LendaSwapOptions
        {
            ApiUrl = "https://api.test.lendaswap.com"
        });
        return new LendaSwapClient(httpClient, options);
    }

    [Test]
    public async Task GetTokensAsync_DeserializesResponse()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {
            "btc_tokens": [{"token_id": "btc", "symbol": "BTC", "name": "Bitcoin", "decimals": 8, "chain": "Bitcoin"}],
            "evm_tokens": [{"token_id": "0x3c499c", "symbol": "USDC", "name": "USD Coin", "decimals": 6, "chain": "137"}]
        }
        """);

        var client = CreateClient(handler);
        var tokens = await client.GetTokensAsync();

        Assert.That(tokens.BtcTokens, Has.Count.EqualTo(1));
        Assert.That(tokens.EvmTokens, Has.Count.EqualTo(1));
        Assert.That(tokens.EvmTokens[0].Symbol, Is.EqualTo("USDC"));
    }

    [Test]
    public async Task GetQuoteAsync_DeserializesResponse()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {
            "exchange_rate": "96500.00",
            "protocol_fee": 250,
            "protocol_fee_rate": 0.0025,
            "network_fee": 150,
            "gasless_network_fee": 50,
            "source_amount": 100000,
            "target_amount": 96100,
            "min_amount": 10000,
            "max_amount": 10000000
        }
        """);

        var client = CreateClient(handler);
        var quote = await client.GetQuoteAsync("Arkade", "btc", "137", "0x3c499c", sourceAmount: 100000);

        Assert.That(quote.SourceAmount, Is.EqualTo(100000));
        Assert.That(quote.TargetAmount, Is.EqualTo(96100));
        Assert.That(quote.MinAmount, Is.EqualTo(10000));
    }

    [Test]
    public async Task ApiKey_SentAsHeader_WhenConfigured()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, """{"btc_tokens":[],"evm_tokens":[]}""");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.test.lendaswap.com") };
        var options = Options.Create(new LendaSwapOptions
        {
            ApiUrl = "https://api.test.lendaswap.com",
            ApiKey = "test-key-123"
        });
        var client = new LendaSwapClient(httpClient, options);

        await client.GetTokensAsync();

        Assert.That(handler.LastRequest!.Headers.GetValues("X-API-Key").First(), Is.EqualTo("test-key-123"));
    }
}

// Simple mock handler for unit tests
internal class MockHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _responseBody;
    public HttpRequestMessage? LastRequest { get; private set; }

    public MockHttpHandler(HttpStatusCode statusCode, string responseBody)
    {
        _statusCode = statusCode;
        _responseBody = responseBody;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
        });
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test NArk.Tests/ --filter "FullyQualifiedName~LendaSwapClientTests" -v n`
Expected: Build error — types don't exist.

**Step 3: Create options class**

```csharp
// NArk.Swaps/LendaSwap/LendaSwapOptions.cs
namespace NArk.Swaps.LendaSwap;

public class LendaSwapOptions
{
    public required string ApiUrl { get; set; }
    public string? ApiKey { get; set; }
}
```

**Step 4: Create models**

```csharp
// NArk.Swaps/LendaSwap/Models/LendaSwapResponses.cs
using System.Text.Json.Serialization;

namespace NArk.Swaps.LendaSwap.Models;

public record TokenInfosResponse
{
    [JsonPropertyName("btc_tokens")]
    public required List<TokenInfo> BtcTokens { get; init; }

    [JsonPropertyName("evm_tokens")]
    public required List<TokenInfo> EvmTokens { get; init; }
}

public record TokenInfo
{
    [JsonPropertyName("token_id")]
    public required string TokenId { get; init; }

    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("decimals")]
    public required int Decimals { get; init; }

    [JsonPropertyName("chain")]
    public required string Chain { get; init; }
}

public record QuoteResponse
{
    [JsonPropertyName("exchange_rate")]
    public required string ExchangeRate { get; init; }

    [JsonPropertyName("protocol_fee")]
    public required long ProtocolFee { get; init; }

    [JsonPropertyName("protocol_fee_rate")]
    public required decimal ProtocolFeeRate { get; init; }

    [JsonPropertyName("network_fee")]
    public required long NetworkFee { get; init; }

    [JsonPropertyName("gasless_network_fee")]
    public required long GaslessNetworkFee { get; init; }

    [JsonPropertyName("source_amount")]
    public required long SourceAmount { get; init; }

    [JsonPropertyName("target_amount")]
    public required long TargetAmount { get; init; }

    [JsonPropertyName("min_amount")]
    public required long MinAmount { get; init; }

    [JsonPropertyName("max_amount")]
    public required long MaxAmount { get; init; }
}

public record GetSwapStatusResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("source_amount")]
    public long SourceAmount { get; init; }

    [JsonPropertyName("target_amount")]
    public long TargetAmount { get; init; }

    [JsonPropertyName("fee_sats")]
    public long FeeSats { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    // BTC addresses
    [JsonPropertyName("btc_htlc_address")]
    public string? BtcHtlcAddress { get; init; }

    [JsonPropertyName("btc_fund_txid")]
    public string? BtcFundTxId { get; init; }

    [JsonPropertyName("btc_claim_txid")]
    public string? BtcClaimTxId { get; init; }

    // Arkade addresses
    [JsonPropertyName("arkade_vhtlc_address")]
    public string? ArkadeVhtlcAddress { get; init; }

    [JsonPropertyName("arkade_fund_txid")]
    public string? ArkadeFundTxId { get; init; }

    [JsonPropertyName("arkade_claim_txid")]
    public string? ArkadeClaimTxId { get; init; }

    // EVM addresses
    [JsonPropertyName("evm_htlc_address")]
    public string? EvmHtlcAddress { get; init; }

    [JsonPropertyName("evm_claim_txid")]
    public string? EvmClaimTxId { get; init; }

    [JsonPropertyName("evm_fund_txid")]
    public string? EvmFundTxId { get; init; }

    // VHTLC details
    [JsonPropertyName("vhtlc_refund_locktime")]
    public long VhtlcRefundLocktime { get; init; }

    [JsonPropertyName("btc_refund_locktime")]
    public long BtcRefundLocktime { get; init; }
}
```

```csharp
// NArk.Swaps/LendaSwap/Models/LendaSwapRequests.cs
using System.Text.Json.Serialization;

namespace NArk.Swaps.LendaSwap.Models;

public record BtcToArkadeSwapRequest
{
    [JsonPropertyName("claim_pk")]
    public required string ClaimPk { get; init; }

    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; init; }

    [JsonPropertyName("refund_pk")]
    public required string RefundPk { get; init; }

    [JsonPropertyName("sats_receive")]
    public required long SatsReceive { get; init; }

    [JsonPropertyName("target_arkade_address")]
    public required string TargetArkadeAddress { get; init; }

    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("referral_code")]
    public string? ReferralCode { get; init; }
}

public record ArkadeToEvmSwapRequest
{
    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; init; }

    [JsonPropertyName("refund_pk")]
    public required string RefundPk { get; init; }

    [JsonPropertyName("claiming_address")]
    public required string ClaimingAddress { get; init; }

    [JsonPropertyName("target_address")]
    public required string TargetAddress { get; init; }

    [JsonPropertyName("token_address")]
    public required string TokenAddress { get; init; }

    [JsonPropertyName("evm_chain_id")]
    public required int EvmChainId { get; init; }

    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("amount_in")]
    public long? AmountIn { get; init; }

    [JsonPropertyName("amount_out")]
    public long? AmountOut { get; init; }

    [JsonPropertyName("gasless")]
    public bool? Gasless { get; init; }

    [JsonPropertyName("referral_code")]
    public string? ReferralCode { get; init; }
}

public record EvmToArkadeSwapRequest
{
    [JsonPropertyName("hash_lock")]
    public required string HashLock { get; init; }

    [JsonPropertyName("receiver_pk")]
    public required string ReceiverPk { get; init; }

    [JsonPropertyName("target_address")]
    public required string TargetAddress { get; init; }

    [JsonPropertyName("token_address")]
    public required string TokenAddress { get; init; }

    [JsonPropertyName("evm_chain_id")]
    public required int EvmChainId { get; init; }

    [JsonPropertyName("user_address")]
    public required string UserAddress { get; init; }

    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("amount_in")]
    public long? AmountIn { get; init; }

    [JsonPropertyName("amount_out")]
    public long? AmountOut { get; init; }

    [JsonPropertyName("referral_code")]
    public string? ReferralCode { get; init; }
}
```

**Step 5: Create client base + quote/token methods**

```csharp
// NArk.Swaps/LendaSwap/Client/LendaSwapClient.cs
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NArk.Swaps.LendaSwap.Models;

namespace NArk.Swaps.LendaSwap.Client;

public partial class LendaSwapClient
{
    private readonly HttpClient _httpClient;
    private readonly LendaSwapOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public LendaSwapClient(HttpClient httpClient, IOptions<LendaSwapOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        if (_httpClient.BaseAddress == null)
            _httpClient.BaseAddress = new Uri(_options.ApiUrl);

        if (!string.IsNullOrEmpty(_options.ApiKey))
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
    }
}
```

```csharp
// NArk.Swaps/LendaSwap/Client/LendaSwapClient.Quotes.cs
using System.Net.Http.Json;
using NArk.Swaps.LendaSwap.Models;

namespace NArk.Swaps.LendaSwap.Client;

public partial class LendaSwapClient
{
    public async Task<TokenInfosResponse> GetTokensAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/tokens", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenInfosResponse>(JsonOptions, ct))!;
    }

    public async Task<QuoteResponse> GetQuoteAsync(
        string sourceChain, string sourceToken,
        string targetChain, string targetToken,
        long? sourceAmount = null, long? targetAmount = null,
        CancellationToken ct = default)
    {
        var query = $"/quote?source_chain={sourceChain}&source_token={sourceToken}" +
                    $"&target_chain={targetChain}&target_token={targetToken}";
        if (sourceAmount.HasValue) query += $"&source_amount={sourceAmount}";
        if (targetAmount.HasValue) query += $"&target_amount={targetAmount}";

        var response = await _httpClient.GetAsync(query, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<QuoteResponse>(JsonOptions, ct))!;
    }
}
```

**Step 6: Run tests to verify they pass**

Run: `dotnet test NArk.Tests/ --filter "FullyQualifiedName~LendaSwapClientTests" -v n`
Expected: All 3 tests PASS.

**Step 7: Commit**

```bash
git add NArk.Swaps/LendaSwap/ NArk.Tests/LendaSwapClientTests.cs
git commit -m "feat: add LendaSwap client with token and quote endpoints"
```

---

### Task 12: Add LendaSwap swap creation and status endpoints

**Files:**
- Create: `NArk.Swaps/LendaSwap/Client/LendaSwapClient.Swaps.cs`
- Create: `NArk.Swaps/LendaSwap/Client/LendaSwapClient.Status.cs`
- Modify: `NArk.Tests/LendaSwapClientTests.cs` (add tests)

**Step 1: Write failing tests for swap creation**

```csharp
// Add to LendaSwapClientTests.cs
[Test]
public async Task CreateBtcToArkadeSwapAsync_SendsCorrectRequest()
{
    var handler = new MockHttpHandler(HttpStatusCode.OK, """
    {
        "id": "swap-123",
        "status": "pending",
        "btc_htlc_address": "bcrt1p...",
        "arkade_vhtlc_address": "tark1q...",
        "source_amount": 110000,
        "target_amount": 100000,
        "fee_sats": 250,
        "btc_refund_locktime": 1740000000,
        "vhtlc_refund_locktime": 1740000000,
        "created_at": "2026-02-27T12:00:00Z",
        "network": "regtest"
    }
    """);

    var client = CreateClient(handler);
    var result = await client.CreateBtcToArkadeSwapAsync(new BtcToArkadeSwapRequest
    {
        ClaimPk = "02abc...",
        HashLock = "deadbeef",
        RefundPk = "03def...",
        SatsReceive = 100000,
        TargetArkadeAddress = "tark1q...",
        UserId = "user-1"
    });

    Assert.That(result.Id, Is.EqualTo("swap-123"));
    Assert.That(result.Status, Is.EqualTo("pending"));
    Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Post));
    Assert.That(handler.LastRequest.RequestUri!.PathAndQuery, Is.EqualTo("/swap/bitcoin/arkade"));
}

[Test]
public async Task GetSwapStatusAsync_ReturnsStatus()
{
    var handler = new MockHttpHandler(HttpStatusCode.OK, """
    {
        "id": "swap-123",
        "status": "clientredeemed",
        "direction": "btc_to_arkade",
        "source_amount": 110000,
        "target_amount": 100000,
        "fee_sats": 250
    }
    """);

    var client = CreateClient(handler);
    var status = await client.GetSwapStatusAsync("swap-123");

    Assert.That(status.Status, Is.EqualTo("clientredeemed"));
    Assert.That(status.Direction, Is.EqualTo("btc_to_arkade"));
}
```

**Step 2: Implement swap and status endpoints**

```csharp
// NArk.Swaps/LendaSwap/Client/LendaSwapClient.Swaps.cs
using System.Net.Http.Json;
using NArk.Swaps.LendaSwap.Models;

namespace NArk.Swaps.LendaSwap.Client;

public partial class LendaSwapClient
{
    public async Task<GetSwapStatusResponse> CreateBtcToArkadeSwapAsync(
        BtcToArkadeSwapRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/swap/bitcoin/arkade", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GetSwapStatusResponse>(JsonOptions, ct))!;
    }

    public async Task<GetSwapStatusResponse> CreateArkadeToEvmSwapAsync(
        ArkadeToEvmSwapRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/swap/arkade/evm", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GetSwapStatusResponse>(JsonOptions, ct))!;
    }

    public async Task<GetSwapStatusResponse> CreateEvmToArkadeSwapAsync(
        EvmToArkadeSwapRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/swap/evm/arkade", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GetSwapStatusResponse>(JsonOptions, ct))!;
    }
}
```

```csharp
// NArk.Swaps/LendaSwap/Client/LendaSwapClient.Status.cs
using System.Net.Http.Json;
using NArk.Swaps.LendaSwap.Models;

namespace NArk.Swaps.LendaSwap.Client;

public partial class LendaSwapClient
{
    public async Task<GetSwapStatusResponse> GetSwapStatusAsync(
        string swapId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"/swap/{swapId}", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GetSwapStatusResponse>(JsonOptions, ct))!;
    }
}
```

**Step 3: Run tests**

Run: `dotnet test NArk.Tests/ --filter "FullyQualifiedName~LendaSwapClientTests" -v n`
Expected: All tests PASS.

**Step 4: Commit**

```bash
git add NArk.Swaps/LendaSwap/Client/ NArk.Tests/LendaSwapClientTests.cs
git commit -m "feat: add LendaSwap swap creation and status endpoints"
```

---

## Phase 5: LendaSwap Provider

### Task 13: Create LendaSwapProvider implementing ISwapProvider

**Files:**
- Create: `NArk.Swaps/LendaSwap/LendaSwapProvider.cs`
- Test: `NArk.Tests/LendaSwapProviderTests.cs`

**Step 1: Write failing tests for capability discovery**

```csharp
// NArk.Tests/LendaSwapProviderTests.cs
using NArk.Swaps.Abstractions;
using NArk.Swaps.LendaSwap;

namespace NArk.Tests;

[TestFixture]
public class LendaSwapProviderTests
{
    [Test]
    public void ProviderId_IsLendaswap()
    {
        // Will construct with mocked dependencies
        // For now, verify the static route config
        var provider = CreateProvider();
        Assert.That(provider.ProviderId, Is.EqualTo("lendaswap"));
    }

    [Test]
    public void SupportsRoute_BtcToArk_ReturnsTrue()
    {
        var provider = CreateProvider();
        var route = new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc);
        Assert.That(provider.SupportsRoute(route), Is.True);
    }

    [Test]
    public void SupportsRoute_ArkToEvmPolygon_ReturnsTrueForAnyToken()
    {
        var provider = CreateProvider();
        var route = new SwapRoute(SwapAsset.ArkBtc,
            SwapAsset.Erc20(SwapNetwork.EvmPolygon, "0x3c499c542cEF5E3811e1192ce70d8cC03d5c3359"));
        Assert.That(provider.SupportsRoute(route), Is.True);
    }

    [Test]
    public void SupportsRoute_ArkToLightning_ReturnsFalse()
    {
        var provider = CreateProvider();
        var route = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning);
        Assert.That(provider.SupportsRoute(route), Is.False);
    }

    // Helper to construct provider with mocked deps — fill in during implementation
    private static LendaSwapProvider CreateProvider() => throw new NotImplementedException();
}
```

**Step 2: Implement LendaSwapProvider**

```csharp
// NArk.Swaps/LendaSwap/LendaSwapProvider.cs
using NArk.Swaps.Abstractions;
using NArk.Swaps.LendaSwap.Client;
using NArk.Swaps.LendaSwap.Models;
using NArk.Swaps.Models;
using Microsoft.Extensions.Logging;

namespace NArk.Swaps.LendaSwap;

public class LendaSwapProvider : ISwapProvider
{
    public string ProviderId => "lendaswap";
    public string DisplayName => "LendaSwap";

    private readonly LendaSwapClient _client;
    private readonly ILogger<LendaSwapProvider>? _logger;
    private CancellationTokenSource? _pollCts;

    // LendaSwap supports these network pairs (any ERC-20 token on supported chains)
    private static readonly HashSet<(SwapNetwork, SwapNetwork)> _supportedNetworkPairs = new()
    {
        (SwapNetwork.BitcoinOnchain, SwapNetwork.Ark),
        (SwapNetwork.Ark, SwapNetwork.EvmEthereum),
        (SwapNetwork.Ark, SwapNetwork.EvmPolygon),
        (SwapNetwork.Ark, SwapNetwork.EvmArbitrum),
        (SwapNetwork.EvmEthereum, SwapNetwork.Ark),
        (SwapNetwork.EvmPolygon, SwapNetwork.Ark),
        (SwapNetwork.EvmArbitrum, SwapNetwork.Ark),
        (SwapNetwork.Lightning, SwapNetwork.EvmEthereum),
        (SwapNetwork.Lightning, SwapNetwork.EvmPolygon),
        (SwapNetwork.EvmEthereum, SwapNetwork.Lightning),
        (SwapNetwork.EvmPolygon, SwapNetwork.Lightning),
        (SwapNetwork.BitcoinOnchain, SwapNetwork.EvmEthereum),
        (SwapNetwork.EvmEthereum, SwapNetwork.BitcoinOnchain),
    };

    public LendaSwapProvider(LendaSwapClient client, ILogger<LendaSwapProvider>? logger = null)
    {
        _client = client;
        _logger = logger;
    }

    public bool SupportsRoute(SwapRoute route)
        => _supportedNetworkPairs.Contains((route.Source.Network, route.Destination.Network));

    public async Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct)
    {
        // Query /tokens to get actual available pairs
        var tokens = await _client.GetTokensAsync(ct);
        var routes = new List<SwapRoute>();

        foreach (var (srcNet, dstNet) in _supportedNetworkPairs)
        {
            // For BTC networks, use well-known assets
            var src = srcNet switch
            {
                SwapNetwork.Ark => SwapAsset.ArkBtc,
                SwapNetwork.BitcoinOnchain => SwapAsset.BtcOnchain,
                SwapNetwork.Lightning => SwapAsset.BtcLightning,
                _ => null // EVM — enumerate tokens below
            };

            var dst = dstNet switch
            {
                SwapNetwork.Ark => SwapAsset.ArkBtc,
                SwapNetwork.BitcoinOnchain => SwapAsset.BtcOnchain,
                SwapNetwork.Lightning => SwapAsset.BtcLightning,
                _ => null
            };

            if (src != null && dst != null)
            {
                routes.Add(new SwapRoute(src, dst));
                continue;
            }

            // EVM side — enumerate available tokens for that chain
            var chainId = (src == null ? srcNet : dstNet) switch
            {
                SwapNetwork.EvmEthereum => "1",
                SwapNetwork.EvmPolygon => "137",
                SwapNetwork.EvmArbitrum => "42161",
                _ => null
            };

            if (chainId == null) continue;

            var evmTokens = tokens.EvmTokens.Where(t => t.Chain == chainId);
            foreach (var token in evmTokens)
            {
                var evmNetwork = src == null ? srcNet : dstNet;
                var evmAsset = SwapAsset.Erc20(evmNetwork, token.TokenId);

                routes.Add(src != null
                    ? new SwapRoute(src, evmAsset)
                    : new SwapRoute(evmAsset, dst!));
            }
        }

        return routes.AsReadOnly();
    }

    public event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;

    // Status mapping from LendaSwap statuses to ArkSwapStatus
    private static ArkSwapStatus MapStatus(string status) => status switch
    {
        "pending" or "clientfundingseen" or "clientfunded" or "serverfunded"
            or "clientredeeming" => ArkSwapStatus.Pending,
        "clientredeemed" or "serverredeemed" => ArkSwapStatus.Settled,
        "clientrefunded" or "clientrefundedserverrefunded"
            or "clientrefundedserverfunded" => ArkSwapStatus.Refunded,
        "expired" or "clientinvalidfunded" or "clientfundedtoolate"
            or "clientfundedserverrefunded" => ArkSwapStatus.Failed,
        _ => ArkSwapStatus.Unknown
    };

    public async Task StartAsync(string walletId, CancellationToken ct)
    {
        // Start polling for status updates
        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Poll loop implementation deferred to Task 14
    }

    public Task StopAsync(CancellationToken ct)
    {
        _pollCts?.Cancel();
        return Task.CompletedTask;
    }

    public async Task<SwapResult> CreateSwapAsync(CreateSwapRequest request, CancellationToken ct)
    {
        var route = request.Route;

        // BTC → Ark
        if (route.Source.Network == SwapNetwork.BitcoinOnchain && route.Destination.Network == SwapNetwork.Ark)
        {
            return await CreateBtcToArkSwapAsync(request, ct);
        }

        // Ark → EVM
        if (route.Source.Network == SwapNetwork.Ark && IsEvmNetwork(route.Destination.Network))
        {
            return await CreateArkToEvmSwapAsync((EvmSwapRequest)request, ct);
        }

        // EVM → Ark
        if (IsEvmNetwork(route.Source.Network) && route.Destination.Network == SwapNetwork.Ark)
        {
            return await CreateEvmToArkSwapAsync((EvmSwapRequest)request, ct);
        }

        throw new NotSupportedException($"Route {route} not supported by LendaSwap");
    }

    public Task RefundSwapAsync(string walletId, string swapId, CancellationToken ct)
    {
        // LendaSwap refunds handled via VHTLC/HTLC timeout paths
        throw new NotImplementedException("LendaSwap refund not yet implemented");
    }

    public async Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct)
    {
        var (srcChain, srcToken, dstChain, dstToken) = RouteToChainTokens(route);
        var quote = await _client.GetQuoteAsync(srcChain, srcToken, dstChain, dstToken,
            sourceAmount: 100000, ct: ct);

        return new SwapLimits
        {
            Route = route,
            MinAmount = quote.MinAmount,
            MaxAmount = quote.MaxAmount,
            FeePercentage = quote.ProtocolFeeRate,
            MinerFee = quote.NetworkFee,
        };
    }

    public async Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct)
    {
        var (srcChain, srcToken, dstChain, dstToken) = RouteToChainTokens(route);
        var quote = await _client.GetQuoteAsync(srcChain, srcToken, dstChain, dstToken,
            sourceAmount: amount, ct: ct);

        return new SwapQuote
        {
            Route = route,
            SourceAmount = quote.SourceAmount,
            DestinationAmount = quote.TargetAmount,
            TotalFees = quote.ProtocolFee + quote.NetworkFee,
            ExchangeRate = decimal.Parse(quote.ExchangeRate),
        };
    }

    public ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        return ValueTask.CompletedTask;
    }

    // Private helpers for swap creation and route mapping
    // (implementations below reference LendaSwap API spec)

    private static bool IsEvmNetwork(SwapNetwork n) =>
        n is SwapNetwork.EvmEthereum or SwapNetwork.EvmPolygon or SwapNetwork.EvmArbitrum;

    private static int ToChainId(SwapNetwork n) => n switch
    {
        SwapNetwork.EvmEthereum => 1,
        SwapNetwork.EvmPolygon => 137,
        SwapNetwork.EvmArbitrum => 42161,
        _ => throw new ArgumentException($"Not an EVM network: {n}")
    };

    private static (string srcChain, string srcToken, string dstChain, string dstToken)
        RouteToChainTokens(SwapRoute route)
    {
        string ChainStr(SwapAsset a) => a.Network switch
        {
            SwapNetwork.Ark => "Arkade",
            SwapNetwork.BitcoinOnchain => "Bitcoin",
            SwapNetwork.Lightning => "Lightning",
            SwapNetwork.EvmEthereum => "1",
            SwapNetwork.EvmPolygon => "137",
            SwapNetwork.EvmArbitrum => "42161",
            _ => throw new ArgumentException($"Unknown network: {a.Network}")
        };

        string TokenStr(SwapAsset a) => a.Network switch
        {
            SwapNetwork.Ark or SwapNetwork.BitcoinOnchain or SwapNetwork.Lightning => "btc",
            _ => a.AssetId // ERC-20 contract address
        };

        return (ChainStr(route.Source), TokenStr(route.Source),
                ChainStr(route.Destination), TokenStr(route.Destination));
    }

    private async Task<SwapResult> CreateBtcToArkSwapAsync(CreateSwapRequest request, CancellationToken ct)
    {
        // TODO: Generate claim/refund keys from wallet, compute hash lock
        // For now, create the swap and return the result
        throw new NotImplementedException("BTC→Ark swap creation needs key derivation integration");
    }

    private async Task<SwapResult> CreateArkToEvmSwapAsync(EvmSwapRequest request, CancellationToken ct)
    {
        throw new NotImplementedException("Ark→EVM swap creation needs VHTLC integration");
    }

    private async Task<SwapResult> CreateEvmToArkSwapAsync(EvmSwapRequest request, CancellationToken ct)
    {
        throw new NotImplementedException("EVM→Ark swap creation needs EVM HTLC integration");
    }
}
```

**Step 3: Fill in test helper and run tests**

Update `CreateProvider()` in the test file to construct with mocked deps:

```csharp
private static LendaSwapProvider CreateProvider()
{
    var handler = new MockHttpHandler(HttpStatusCode.OK, "{}");
    var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.api") };
    var options = Options.Create(new LendaSwapOptions { ApiUrl = "https://test.api" });
    var client = new LendaSwapClient(httpClient, options);
    return new LendaSwapProvider(client);
}
```

Run: `dotnet test NArk.Tests/ --filter "FullyQualifiedName~LendaSwapProviderTests" -v n`
Expected: All 4 tests PASS.

**Step 4: Commit**

```bash
git add NArk.Swaps/LendaSwap/LendaSwapProvider.cs NArk.Tests/LendaSwapProviderTests.cs
git commit -m "feat: add LendaSwapProvider implementing ISwapProvider"
```

---

### Task 14: Add LendaSwap status polling loop

**Files:**
- Modify: `NArk.Swaps/LendaSwap/LendaSwapProvider.cs`

**Step 1: Implement polling in StartAsync**

Add a background polling loop that periodically checks active swap statuses via `GET /swap/{id}` and fires `SwapStatusChanged` when status transitions.

Pattern should match the existing Boltz polling approach (poll every 30-60 seconds for active swaps, fire events on transitions).

**Step 2: Verify build**

Run: `dotnet build NArk.Swaps/`

**Step 3: Commit**

```bash
git add NArk.Swaps/LendaSwap/LendaSwapProvider.cs
git commit -m "feat: add LendaSwap status polling loop"
```

---

### Task 15: Add LendaSwap DI registration

**Files:**
- Modify: `NArk.Swaps/Hosting/SwapServiceCollectionExtensions.cs`
- Modify: `NArk.Swaps/Hosting/SwapApplicationBuilderExtensions.cs`

**Step 1: Add AddLendaSwapProvider extension**

```csharp
public static IServiceCollection AddLendaSwapProvider(
    this IServiceCollection services,
    Action<LendaSwapOptions>? configure = null)
{
    if (configure != null)
        services.Configure(configure);

    services.AddSingleton<LendaSwapClient>();
    services.AddSingleton<ISwapProvider, LendaSwapProvider>();
    services.AddHttpClient<LendaSwapClient>();

    return services;
}
```

**Step 2: Add builder extension**

```csharp
public static ArkClientBuilder WithLendaSwap(this ArkClientBuilder builder,
    string apiUrl, string? apiKey = null)
{
    builder.Services.AddLendaSwapProvider(opts =>
    {
        opts.ApiUrl = apiUrl;
        opts.ApiKey = apiKey;
    });
    return builder;
}
```

**Step 3: Verify build**

Run: `dotnet build NArk.Swaps/`

**Step 4: Commit**

```bash
git add NArk.Swaps/Hosting/
git commit -m "feat: add LendaSwap DI registration extensions"
```

---

## Phase 6: Router Unit Tests

### Task 16: Add unit tests for SwapsManagementService routing

**Files:**
- Create: `NArk.Tests/SwapRoutingTests.cs`

**Step 1: Write routing tests**

```csharp
// NArk.Tests/SwapRoutingTests.cs
using NArk.Swaps.Abstractions;
using NArk.Swaps.Services;

namespace NArk.Tests;

[TestFixture]
public class SwapRoutingTests
{
    [Test]
    public void GetAvailableRoutes_AggregatesAllProviders()
    {
        // Create mock providers with different route sets
        var boltz = CreateMockProvider("boltz", new[]
        {
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning),
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcOnchain),
        });
        var lenda = CreateMockProvider("lendaswap", new[]
        {
            new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc),
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.Erc20(SwapNetwork.EvmPolygon, "0xUSDC")),
        });

        var sms = CreateRouter(boltz, lenda);
        var routes = sms.GetAvailableRoutes();

        Assert.That(routes, Has.Count.EqualTo(4));
    }

    [Test]
    public void GetProvidersForRoute_ReturnsMatchingProviders()
    {
        var boltz = CreateMockProvider("boltz", new[]
        {
            new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc),
        });
        var lenda = CreateMockProvider("lendaswap", new[]
        {
            new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc), // Same route!
        });

        var sms = CreateRouter(boltz, lenda);
        var providers = sms.GetProvidersForRoute(new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc));

        Assert.That(providers.Count(), Is.EqualTo(2));
    }

    [Test]
    public void ResolveProvider_PrefersRequested()
    {
        var boltz = CreateMockProvider("boltz", new[]
        {
            new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc),
        });
        var lenda = CreateMockProvider("lendaswap", new[]
        {
            new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc),
        });

        var sms = CreateRouter(boltz, lenda);
        // Test that PreferredProviderId selects the right one
        // (needs internal access or public method)
    }

    [Test]
    public void ResolveProvider_ThrowsWhenNoProviderSupportsRoute()
    {
        var boltz = CreateMockProvider("boltz", new[]
        {
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning),
        });

        var sms = CreateRouter(boltz);
        var route = new SwapRoute(SwapAsset.ArkBtc, SwapAsset.Erc20(SwapNetwork.EvmPolygon, "0xUSDC"));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sms.InitiateSwapAsync(new SimpleSwapRequest
            {
                WalletId = "test",
                Route = route,
                Amount = 100000,
            }, CancellationToken.None));
    }

    // Helpers — use NSubstitute or manual mocks
    private static ISwapProvider CreateMockProvider(string id, SwapRoute[] routes)
    {
        // Return a mock ISwapProvider that reports the given routes
        throw new NotImplementedException("Use NSubstitute");
    }

    private static SwapsManagementService CreateRouter(params ISwapProvider[] providers)
    {
        // Construct with mocked shared deps + the given providers
        throw new NotImplementedException("Needs constructor update");
    }
}
```

**Step 2: Implement tests with actual mocking framework**

Fill in `CreateMockProvider` and `CreateRouter` using NSubstitute (already in test project dependencies) or manual mocks.

**Step 3: Run tests**

Run: `dotnet test NArk.Tests/ --filter "FullyQualifiedName~SwapRoutingTests" -v n`
Expected: All tests PASS.

**Step 4: Commit**

```bash
git add NArk.Tests/SwapRoutingTests.cs
git commit -m "test: add unit tests for swap routing and provider resolution"
```

---

## Phase 7: E2E Tests

### Task 17: Verify existing Boltz E2E tests pass (regression)

**Step 1: Run all E2E tests**

Run: `dotnet test NArk.Tests.End2End/ -v n`
Expected: All existing Boltz swap tests pass unchanged.

**Step 2: Fix any regressions**

If tests fail, the issue is in the refactored `SwapsManagementService` or `BoltzSwapProvider`. Debug and fix before proceeding.

**Step 3: Commit fixes if any**

```bash
git add -A && git commit -m "fix: resolve E2E regression from provider extraction"
```

---

### Task 18: Add LendaSwap E2E tests

**Files:**
- Create: `NArk.Tests.End2End/LendaSwapTests.cs`
- Modify: `NArk.Tests.End2End/Infrastructure/docker-compose.ark.yml` (if LendaSwap has a docker image)

**Step 1: Determine LendaSwap test infrastructure**

Check if LendaSwap provides:
1. A Docker image for self-hosted regtest mode → add to docker-compose
2. A public testnet endpoint → use directly
3. Neither → create a mock HTTP server for E2E tests

**Step 2: Write LendaSwap E2E test**

Follow existing pattern from `SwapManagementServiceTests.cs`:

```csharp
// NArk.Tests.End2End/LendaSwapTests.cs
namespace NArk.Tests.End2End.Swaps;

[TestFixture]
public class LendaSwapTests
{
    // Endpoint will depend on infrastructure decision
    public static readonly Uri LendaSwapEndpoint = new("http://localhost:XXXX");

    [Test]
    public async Task CanGetQuoteFromLendaSwap()
    {
        // Basic connectivity test — verify client can talk to LendaSwap
        var httpClient = new HttpClient { BaseAddress = LendaSwapEndpoint };
        var options = Options.Create(new LendaSwapOptions { ApiUrl = LendaSwapEndpoint.ToString() });
        var client = new LendaSwapClient(httpClient, options);

        var tokens = await client.GetTokensAsync();
        Assert.That(tokens.BtcTokens, Is.Not.Empty);
    }

    [Test]
    public async Task CanDoBtcToArkSwapViaLendaSwap()
    {
        // Full swap flow — depends on LendaSwap test infrastructure
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        // ... follows existing test pattern from SwapManagementServiceTests
    }

    [Test]
    public async Task CanDoArkToEvmSwapViaLendaSwap()
    {
        // Ark → EVM swap — needs EVM test infrastructure too
        // May need to be marked [Explicit] if no EVM testnet available
    }
}
```

**Step 3: Run LendaSwap E2E tests**

Run: `dotnet test NArk.Tests.End2End/ --filter "FullyQualifiedName~LendaSwapTests" -v n`

**Step 4: Commit**

```bash
git add NArk.Tests.End2End/LendaSwapTests.cs
git commit -m "test: add LendaSwap E2E tests"
```

---

## Phase 8: PR and CI

### Task 19: Create PR against master

**Step 1: Push branch**

```bash
git push -u origin multiswap-provider
```

**Step 2: Create PR**

```bash
gh pr create --title "feat: multi-provider swap architecture with LendaSwap" --body "$(cat <<'EOF'
## Summary
- Extract `ISwapProvider` interface with capability-based route discovery
- Refactor `SwapsManagementService` into a multi-provider router
- Extract existing Boltz logic into `BoltzSwapProvider`
- Add `LendaSwapProvider` wrapping LendaSwap REST API
- Add `SwapRoute(SwapAsset, SwapAsset)` model for network+asset pair routing
- Support multiple simultaneous providers resolved by route + preference

## Design
See `docs/plans/2026-02-27-multi-provider-swaps-design.md`

## Test plan
- [ ] All existing Boltz E2E tests pass (regression)
- [ ] New unit tests: routing, capability discovery, LendaSwap client mocks
- [ ] New E2E tests: LendaSwap swap flows
- [ ] CI build passes
EOF
)"
```

**Step 3: Wait for CI, fix failures**

Monitor CI with: `gh pr checks --watch`

Fix any CI failures and push fixes.

### Task 20: Address review comments

Iterate on review feedback until PR is approved and CI is green.

```bash
# After making changes from review
git add -A && git commit -m "address review feedback"
git push
```

---

## Dependency Graph

```
Task 1 (SwapRoute types) ──┐
Task 2 (ISwapProvider)   ───┤
Task 3 (Request/Result)  ───┼── Task 4 (ArkSwap extension)
                            │
                            ├── Task 5 (BoltzProvider skeleton) ── Task 6 (Create) ── Task 7 (Poll/Claim)
                            │                                                              │
                            │                                                              v
                            │                                           Task 8 (Router refactor) ── Task 9 (DI)
                            │                                                              │
                            │                                                              v
                            │                                           Task 10 (E2E regression check)
                            │
                            ├── Task 11 (LendaSwap client) ── Task 12 (Swap+Status endpoints)
                            │                                              │
                            │                                              v
                            │                               Task 13 (LendaSwapProvider) ── Task 14 (Polling)
                            │                                              │
                            │                                              v
                            │                               Task 15 (LendaSwap DI)
                            │
                            └── Task 16 (Router unit tests)
                                Task 17 (Boltz E2E regression)
                                Task 18 (LendaSwap E2E)
                                Task 19 (PR + CI)
                                Task 20 (Review)
```

Tasks 1-3 are independent and can be done in parallel.
Tasks 5-10 (Boltz extraction) and Tasks 11-15 (LendaSwap) can be parallelized after Task 4.
Tasks 16-18 (tests) depend on both tracks completing.
