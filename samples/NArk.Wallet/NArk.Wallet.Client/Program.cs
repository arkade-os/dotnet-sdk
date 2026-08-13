using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Abstractions.Intents;
using NArk.Arkade.Hosting;
using NArk.Core.Services;
using NArk.Core.Wallet;
using NArk.Core.Payments;
using NArk.Hosting;
using NArk.Storage.EfCore.Hosting;
using NArk.Wallet.Client;
using NArk.Wallet.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Logging.AddFilter("NArk", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

// ── Network ──
var networkConfig = ArkNetworkConfig.Mutinynet;

// ── EF Core + SQLite via Bit.Besql (persistent via browser Cache API) ──
builder.Services.AddBesqlDbContextFactory<WalletDbContext>(options =>
{
    options.UseSqlite("Data Source=ArkadeWallet.db");
});
builder.Services.AddArkEfCoreStorage<WalletDbContext>();
builder.Services.AddArkPaymentTracking();

// ── NArk SDK core services ──
builder.Services.AddArkCoreServices();
builder.Services.AddArkRestTransport(networkConfig);

// ── NArk SDK swap services ──
builder.Services.AddArkSwapServices();
// In full ASP.NET hosts, AddHttpClient<BoltzClient>() provides the HttpClient. In WASM we must
// register CachedBoltzClient (and its BoltzClient base) with a plain HttpClient ourselves.
builder.Services.AddSingleton<NArk.Swaps.Boltz.Client.CachedBoltzClient>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NArk.Swaps.Boltz.Models.BoltzClientOptions>>();
    return new NArk.Swaps.Boltz.Client.CachedBoltzClient(new HttpClient(), opts);
});
builder.Services.AddSingleton<NArk.Swaps.Boltz.Client.BoltzClient>(sp =>
    sp.GetRequiredService<NArk.Swaps.Boltz.Client.CachedBoltzClient>());

// ── Arkade asset swaps (covenant-based BTC⇄asset via the solver market) ──
// The maker funds a covenant offer (TLV offer packet in the funding tx); a solver on the
// public market fulfils it. Needs the network emulator (covenant co-signer whose key the
// offer embeds) + solver-registry discovery. Emulator URL is per-network; Mutinynet's is:
builder.Services.AddEmulatorClient(opts =>
    opts.ServerUrl = networkConfig == ArkNetworkConfig.Mainnet
        ? "https://emulator.arkade.sh"
        : "https://emulator.mutinynet.arkade.sh");
// AddEmulatorClient pins a SocketsHttpHandler, which is right on a server and unusable here:
// the browser runtime has no sockets to pool, and merely setting PooledConnectionLifetime throws
// PlatformNotSupportedException from inside the DI factory — surfacing as a component that fails
// to render rather than as anything naming this line. Overriding the registration afterwards is
// the same move already made above for CachedBoltzClient and below for SolverDiscoveryService;
// the browser owns the connections, so the default handler is the correct one.
builder.Services.AddSingleton(sp => new NArk.Arkade.Emulator.EmulatorClient(
    new HttpClient(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NArk.Arkade.Emulator.EmulatorClientOptions>>()));
// SolverDiscoveryService has multiple ctors, so ActivatorUtilities (AddHttpClient<T>) can't pick one
// in WASM — register explicitly with a plain HttpClient, mirroring the CachedBoltzClient registration.
builder.Services.AddSingleton(sp => new NArk.ArkadeIntents.Services.SolverDiscoveryService(
    new HttpClient(),
    sp.GetService<ILogger<NArk.ArkadeIntents.Services.SolverDiscoveryService>>()));
builder.Services.AddSingleton<NArk.ArkadeIntents.Assets.AssetIntentsManager>();
// Watches pending swaps' covenant VTXOs and transitions their status (filled by a solver / cancelled).
builder.Services.AddSingleton<NArk.ArkadeIntents.Services.ArkadeSwapIntentMonitoringService>();

// ── Arkade Lightning corridors (arkade:BTC⇄lightning:BTC) ──
// These replace the Boltz submarine/reverse swaps this sample used to run. Same job — pay an
// invoice out of Arkade, or be paid over Lightning into it — but the counterparty is a solver
// reached per swap over RFQ, and a swap is settled by a covenant rather than by an account.
// Boltz stays wired below for the chain swaps, which have no intent corridor yet.
builder.Services.AddSingleton<NArk.ArkadeIntents.Lightning.LightningIntentsClient>();
// The receive corridor seals the swap preimage with AES-256-GCM, which the browser's .NET runtime
// does not implement — so the browser's own is handed in. Everything else in the packet (ECDH,
// HKDF) runs fine here; this is the single primitive that does not.
builder.Services.AddSingleton<NArk.ArkadeIntents.Lightning.IAesGcmCipher>(sp =>
    new WebCryptoAesGcmCipher(sp.GetRequiredService<IJSRuntime>()));
builder.Services.AddSingleton(sp => new NArk.ArkadeIntents.Lightning.LightningIntentsClient(
    sp.GetRequiredService<NArk.Core.Transport.IClientTransport>(),
    sp.GetRequiredService<NArk.Arkade.Emulator.IEmulatorProvider>(),
    sp.GetRequiredService<NArk.Core.Services.IContractService>(),
    sp.GetRequiredService<NArk.Core.Services.ISpendingService>(),
    sp.GetRequiredService<NArk.ArkadeIntents.IArkadeIntentStorage>(),
    sp.GetRequiredService<NArk.Abstractions.Contracts.IContractStorage>(),
    sp.GetRequiredService<NArk.Abstractions.VTXOs.IVtxoStorage>(),
    sp.GetRequiredService<IWalletProvider>(),
    // Named, so a further constructor parameter cannot silently take the cipher's place.
    cipher: sp.GetRequiredService<NArk.ArkadeIntents.Lightning.IAesGcmCipher>(),
    logger: sp.GetService<ILogger<NArk.ArkadeIntents.Lightning.LightningIntentsClient>>()));
builder.Services.AddSingleton<NArk.ArkadeIntents.Services.ArkadeIntentsService>();
// One solver, named outright: a registry market entry carries no relay or key for the solver
// behind it, so there is nothing to dial from discovery alone. Swap in your own solver's key.
// No solver is named here. Which ones exist is answered by the public registry at runtime, so
// nothing about a counterparty is baked into this build.
builder.Services.AddSingleton(new ArkadeLightningOptions
{
    CovclaimdUrl = builder.Configuration["ArkadeLightning:CovclaimdUrl"] is { Length: > 0 } covclaimd
        ? new Uri(covclaimd)
        : null,
    // Development-only, from an untracked appsettings.Development.json. Empty in the repository.
    SolverNostrPubkeyOverride = builder.Configuration["ArkadeLightning:SolverNostrPubkey"],
});
builder.Services.AddSingleton(sp => new ArkadeLightningService(
    sp.GetRequiredService<ArkadeLightningOptions>(),
    sp.GetRequiredService<NArk.ArkadeIntents.Services.ArkadeIntentsService>(),
    sp.GetRequiredService<NArk.ArkadeIntents.Services.SolverDiscoveryService>(),
    networkConfig == ArkNetworkConfig.Mainnet ? "bitcoin" : "mutinynet"));

// ── SDK infrastructure ──
builder.Services.Configure<NArk.Core.Models.Options.SimpleIntentSchedulerOptions>(opts =>
{
    // Trigger re-boarding for VTXOs expiring within 7 days.
    // Boarding UTXOs (Unrolled=true) are always batched regardless of this threshold.
    opts.Threshold = TimeSpan.FromDays(1);
});

if (networkConfig == ArkNetworkConfig.Mutinynet)
{
    builder.Services.Configure<NArk.Core.Models.Options.IntentGenerationServiceOptions>(opts =>
    {
        opts.PollInterval = TimeSpan.FromSeconds(30);
    });
}

builder.Services.AddSingleton<IIntentScheduler, SimpleIntentScheduler>();
builder.Services.AddSingleton<ISafetyService, WasmSafetyService>();
builder.Services.AddSingleton<IBitcoinBlockchain>(sp =>
{
    if (!string.IsNullOrWhiteSpace(networkConfig.EsploraUri))
    {
        var baseUri = networkConfig.EsploraUri.TrimEnd('/') + "/";
        return new EsploraBlockchain(new Uri(baseUri));
    }
    return new FallbackChainTimeProvider();
});
builder.Services.AddSingleton<IWalletProvider, DefaultWalletProvider>();
builder.Services.AddSingleton<IAssetManager, AssetManager>();

// ── Boarding UTXO sync (polls the chain for confirmed boarding UTXOs) ──
builder.Services.AddSingleton<BoardingUtxoSyncService>();
builder.Services.AddSingleton<BoardingUtxoPollService>();

// ── Wallet service (replaces gateway API client) ──
builder.Services.AddSingleton<ArkWalletService>();
builder.Services.AddSingleton<WalletState>();
builder.Services.AddSingleton(new LnurlHelper(new HttpClient()));

var host = builder.Build();

// Bring the SQLite schema up to the model. EnsureCreatedAsync alone is not enough: it creates
// the schema only when the database is absent, so a wallet carried over from an earlier build
// keeps its old schema forever and every table added since is simply missing — which surfaces
// far from here, as "no such table" from whichever query needs it first.
var dbFactory = host.Services.GetRequiredService<IDbContextFactory<WalletDbContext>>();
await using var db = await dbFactory.CreateDbContextAsync();
await db.Database.EnsureCreatedAsync();
await SchemaBootstrapper.CreateMissingTablesAsync(db);

// Start SDK lifecycle services manually (WASM has no IHostedService support)
await host.Services.StartArkServicesAsync();

await host.RunAsync();

