# Multi-Provider Swap Architecture

**Date:** 2026-02-27
**Status:** Proposed
**Scope:** NArk.Swaps package — extract provider abstraction, add LendaSwap as second provider

## Problem

The swap package (`NArk.Swaps`) is tightly coupled to Boltz. `SwapsManagementService` directly
depends on `BoltzSwapsService`, `CachedBoltzClient`, and `ChainSwapMusigSession`. Adding a second
swap provider (LendaSwap) requires an abstraction layer that allows multiple providers to coexist.

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Architecture | Provider interface + swap router | Clean separation; shared orchestration; avoids duplication |
| Multi-provider | Multiple simultaneous providers | Router resolves by route + optional preference |
| Capability model | Dynamic capability-based discovery | Providers declare supported routes; routes are asset-aware |
| Route model | `SwapRoute(SwapAsset, SwapAsset)` | Network + asset pairs; handles EVM tokens, Ark assets, same-network swaps |
| EVM scope | First-class in abstraction | LendaSwap's primary differentiator; model it now to avoid redesign |
| LendaSwap client | Hand-written, spec-informed | Consistent with BoltzClient style; use OpenAPI spec as reference |
| Request/Response | Inheritance hierarchy | Base class with route-agnostic fields; subclasses add destination-specific data |

## Core Abstractions

### SwapNetwork

```csharp
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

### SwapAsset

```csharp
public record SwapAsset(SwapNetwork Network, string AssetId)
{
    // Well-known Bitcoin assets
    public static readonly SwapAsset BtcOnchain = new(SwapNetwork.BitcoinOnchain, "BTC");
    public static readonly SwapAsset BtcLightning = new(SwapNetwork.Lightning, "BTC");
    public static readonly SwapAsset ArkBtc = new(SwapNetwork.Ark, "BTC");

    // Factory methods for dynamic assets
    public static SwapAsset Erc20(SwapNetwork chain, string contractAddress)
        => new(chain, contractAddress);

    public static SwapAsset ArkAsset(string assetId)
        => new(SwapNetwork.Ark, assetId);
}
```

### SwapRoute

```csharp
public record SwapRoute(SwapAsset Source, SwapAsset Destination);
```

**Examples:**
- `SwapRoute(ArkBtc, BtcOnchain)` — Ark BTC to on-chain BTC (Boltz chain swap)
- `SwapRoute(ArkBtc, Erc20(EvmPolygon, "0x3c499c..."))` — Ark BTC to Polygon USDC (LendaSwap)
- `SwapRoute(ArkAsset("abc"), ArkBtc)` — same-network Ark asset swap
- `SwapRoute(Erc20(EvmEthereum, "0xA0b8..."), ArkBtc)` — ETH USDC to Ark BTC

### ISwapProvider

```csharp
public interface ISwapProvider : IAsyncDisposable
{
    string ProviderId { get; }
    string DisplayName { get; }

    // Capability discovery
    bool SupportsRoute(SwapRoute route);
    Task<IReadOnlyCollection<SwapRoute>> GetAvailableRoutesAsync(CancellationToken ct);

    // Lifecycle
    Task StartAsync(string walletId, CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    // Swap operations
    Task<SwapResult> CreateSwapAsync(CreateSwapRequest request, CancellationToken ct);
    Task RefundSwapAsync(string walletId, string swapId, CancellationToken ct);

    // Pricing & limits
    Task<SwapLimits> GetLimitsAsync(SwapRoute route, CancellationToken ct);
    Task<SwapQuote> GetQuoteAsync(SwapRoute route, long amount, CancellationToken ct);

    // Events
    event EventHandler<SwapStatusChangedEvent>? SwapStatusChanged;
}
```

## Request/Response Hierarchy

### Requests

```csharp
public abstract record CreateSwapRequest
{
    public required string WalletId { get; init; }
    public required SwapRoute Route { get; init; }
    public required long Amount { get; init; }
    public string? PreferredProviderId { get; init; }
}

// Ark->Lightning, EVM->Lightning — need an invoice to pay
public record LightningSwapRequest : CreateSwapRequest
{
    public required string Invoice { get; init; }
}

// Ark->EVM, Lightning->EVM, BTC->EVM — need EVM destination + token
public record EvmSwapRequest : CreateSwapRequest
{
    public required string EvmAddress { get; init; }
    public required string TokenContract { get; init; }
}

// Ark->BTC — optionally specify destination BTC address
public record OnchainSwapRequest : CreateSwapRequest
{
    public string? DestinationAddress { get; init; }
}

// BTC->Ark, Lightning->Ark, EVM->Ark — provider handles deposit address
public record SimpleSwapRequest : CreateSwapRequest;
```

### Results

```csharp
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

// Provider returns a deposit address (BTC->Ark, EVM->Ark)
public record DepositSwapResult : SwapResult
{
    public required string DepositAddress { get; init; }
}

// Provider returns a Lightning invoice to pay (Reverse submarine)
public record InvoiceSwapResult : SwapResult
{
    public required string Invoice { get; init; }
}

// Provider creates a VHTLC contract (Ark->Lightning, Ark->BTC)
public record VhtlcSwapResult : SwapResult
{
    public required string ContractScript { get; init; }
    public required string ContractAddress { get; init; }
}
```

### Pricing

```csharp
public record SwapLimits
{
    public required SwapRoute Route { get; init; }
    public required long MinAmount { get; init; }
    public required long MaxAmount { get; init; }
    public required decimal FeePercentage { get; init; }
    public required long MinerFee { get; init; }
}

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

## Architecture

### Component Diagram

```
SwapsManagementService (Router)
├── Resolves provider by SwapRoute + optional PreferredProviderId
├── Shared concerns: ISwapStorage, VTXO monitoring, intent generation, sweep policy
├── Aggregated capability queries across all providers
│
├── BoltzSwapProvider : ISwapProvider
│   ├── BoltzClient / CachedBoltzClient (HTTP + WebSocket)
│   ├── BoltzSwapsService (VHTLC contract computation)
│   ├── ChainSwapMusigSession (MuSig2 signing)
│   ├── BoltzLimitsValidator (fee/limit validation)
│   └── Status polling loops
│   Supported routes:
│     Ark BTC <-> Lightning BTC
│     Ark BTC <-> BitcoinOnchain BTC
│
└── LendaSwapProvider : ISwapProvider
    ├── LendaSwapClient (HTTP client, spec-informed)
    ├── VHTLC claim/refund for Arkade swaps
    └── EVM HTLC handling
    Supported routes:
      BitcoinOnchain BTC -> Ark BTC
      Ark BTC <-> EVM tokens (ETH, Polygon, Arbitrum)
      Lightning BTC <-> EVM tokens
      BitcoinOnchain BTC <-> EVM tokens
      EVM -> Ark, EVM -> BTC
```

### Boltz Provider Extraction

**Moves into `BoltzSwapProvider`:**
- `BoltzClient` / `CachedBoltzClient` — HTTP API communication
- `BoltzSwapsService` — swap creation with VHTLC contract computation
- `ChainSwapMusigSession` — MuSig2 signing for chain swaps
- `BoltzLimitsValidator` — fee/limit validation
- Status polling loops (currently in SwapsManagementService)
- Boltz WebSocket status subscription

**Stays in `SwapsManagementService` (router):**
- `ISwapStorage` coordination across all providers
- Provider resolution by route + preference
- VTXO monitoring (shared for any VHTLC-based provider)
- Intent generation triggers
- Sweep policy coordination
- Aggregated capability queries

### LendaSwap Provider Structure

```
NArk.Swaps/LendaSwap/
├── Client/
│   ├── LendaSwapClient.cs          // Base HTTP client + options
│   ├── LendaSwapClient.Swaps.cs    // POST /swap/* endpoints
│   ├── LendaSwapClient.Quotes.cs   // POST /quote, GET /tokens
│   └── LendaSwapClient.Status.cs   // GET /swap/{id}
├── Models/
│   ├── LendaSwapRequests.cs        // API request DTOs
│   └── LendaSwapResponses.cs       // API response DTOs
├── LendaSwapProvider.cs            // ISwapProvider implementation
└── LendaSwapOptions.cs             // Configuration (ApiUrl, ApiKey)
```

**LendaSwap REST API endpoints used:**
- `POST /swap/btc/arkade` — BTC to Arkade swap
- `POST /swap/arkade/evm` — Arkade to EVM swap (generic, supports any ERC-20 via 1inch)
- `POST /swap/evm/arkade` — EVM to Arkade swap
- `POST /swap/btc/evm/*` — BTC to EVM swap
- `POST /swap/evm/btc` — EVM to Bitcoin swap
- `POST /swap/evm/lightning` — EVM to Lightning swap
- `GET /swap/{id}` — Get swap status
- `POST /quote` — Get price quote for asset pair
- `GET /tokens` — List available trading pairs

### ArkSwap Model Extension

```csharp
public record ArkSwap
{
    // Existing fields unchanged
    // ...

    // New fields
    public SwapRoute? Route { get; init; }
    public string? ProviderId { get; init; }  // "boltz" or "lendaswap"
}
```

## DI Registration

```csharp
// Core + providers via fluent builder
services.AddArkSwapServices()
    .AddBoltzProvider(opts =>
    {
        opts.BoltzUrl = "https://api.boltz.exchange";
        opts.WebsocketUrl = "wss://api.boltz.exchange";
    })
    .AddLendaSwapProvider(opts =>
    {
        opts.ApiUrl = "https://api.lendaswap.com";
        opts.ApiKey = "optional-api-key";
    });

// Or via application builder extensions
builder.EnableSwaps()
    .WithBoltz(boltzUrl, wsUrl)
    .WithLendaSwap(apiUrl, apiKey);
```

Each `Add*Provider` call registers an `ISwapProvider` implementation.
The router resolves all providers via `IEnumerable<ISwapProvider>`.

## Testing Strategy

### Unit Tests (`NArk.Tests/`)
- `SwapsManagementServiceRoutingTests` — router resolves correct provider by route + preference
- `BoltzSwapProviderTests` — Boltz-specific logic after extraction
- `LendaSwapClientTests` — HTTP client against mocked responses
- `SwapCapabilityTests` — route declarations match actual behavior

### E2E Tests (`NArk.Tests.End2End/`)
- Existing Boltz swap tests remain unchanged (regression)
- New `LendaSwapTests` class in `NArk.Tests.End2End.Swaps` namespace
- Shares `SharedSwapInfrastructure` for arkd + block mining
- LendaSwap test endpoint needed (regtest or mock server in docker-compose)
- Test coverage: BTC->Arkade via LendaSwap, Arkade->EVM (if test infra supports)

### Test Pattern (matching existing style)
```csharp
[TestFixture]
public class LendaSwapTests
{
    [Test]
    public async Task CanDoBtcToArkSwapViaLendaSwap()
    {
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = new InMemorySwapStorage();

        // Create provider directly
        var lendaClient = new LendaSwapClient(httpClient, lendaSwapOptions);
        var provider = new LendaSwapProvider(lendaClient, ...);

        await using var swapMgr = new SwapsManagementService(
            providers: [provider],
            swapStorage, ...shared deps...);

        var completionTcs = new TaskCompletionSource();
        swapStorage.SwapsChanged += (_, swap) =>
        {
            if (swap.Status == ArkSwapStatus.Settled)
                completionTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);
        await swapMgr.InitiateSwapAsync(new SimpleSwapRequest
        {
            WalletId = testingPrerequisite.WalletId,
            Route = new SwapRoute(SwapAsset.BtcOnchain, SwapAsset.ArkBtc),
            Amount = 100_000,
            PreferredProviderId = "lendaswap"
        });

        await completionTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
}
```

## Migration Path

1. **Extract abstractions** — `ISwapProvider`, `SwapRoute`, `SwapAsset`, request/result types into `NArk.Swaps/Abstractions/`
2. **Extract BoltzSwapProvider** — move Boltz-specific logic from `SwapsManagementService` into `BoltzSwapProvider`
3. **Refactor SwapsManagementService** — convert to router pattern, accept `IEnumerable<ISwapProvider>`
4. **Update DI registration** — fluent builder with `AddBoltzProvider()` extension
5. **Implement LendaSwapClient** — hand-written HTTP client against LendaSwap REST API
6. **Implement LendaSwapProvider** — `ISwapProvider` wrapping the client
7. **Add DI registration** — `AddLendaSwapProvider()` extension
8. **Unit tests** — routing, capability discovery, client mocks
9. **E2E tests** — LendaSwap swap flows matching existing test patterns
10. **Update ArkSwap model** — add Route and ProviderId fields

## Risks

- **LendaSwap test infrastructure**: Need regtest/self-hosted mode for E2E tests. If unavailable, E2E tests may need a mock server.
- **VTXO monitoring shared logic**: Both providers may create VHTLCs. The shared monitoring in SwapsManagementService must handle VHTLCs from any provider, not just Boltz.
- **Breaking changes**: Adding `Route` and `ProviderId` to `ArkSwap` is additive. Existing `ArkSwapType` remains for backward compat.
