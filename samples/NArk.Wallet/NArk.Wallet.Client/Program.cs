using Microsoft.AspNetCore.Components.Web;
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
// SolverDiscoveryService has multiple ctors, so ActivatorUtilities (AddHttpClient<T>) can't pick one
// in WASM — register explicitly with a plain HttpClient, mirroring the CachedBoltzClient registration.
builder.Services.AddSingleton(sp => new NArk.ArkadeIntents.Services.SolverDiscoveryService(
    new HttpClient(),
    sp.GetService<ILogger<NArk.ArkadeIntents.Services.SolverDiscoveryService>>()));
builder.Services.AddSingleton<NArk.ArkadeIntents.Services.ArkadeIntentManager>();
// Watches pending swaps' covenant VTXOs and transitions their status (filled by a solver / cancelled).
builder.Services.AddSingleton<NArk.ArkadeIntents.Services.ArkadeSwapIntentMonitoringService>();

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

// Create/migrate the SQLite database on first launch
var dbFactory = host.Services.GetRequiredService<IDbContextFactory<WalletDbContext>>();
await using var db = await dbFactory.CreateDbContextAsync();
await db.Database.EnsureCreatedAsync();

// Start SDK lifecycle services manually (WASM has no IHostedService support)
await host.Services.StartArkServicesAsync();

await host.RunAsync();
