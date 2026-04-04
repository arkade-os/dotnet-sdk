using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Blockchain.NBXplorer;
using NArk.Abstractions.Intents;
using NArk.Core.Services;
using NArk.Core.Wallet;
using NArk.Hosting;
using NArk.Storage.EfCore.Hosting;
using NArk.Swaps.Hosting;
using NArk.Wallet.Client;
using NArk.Wallet.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ── Network ──
var networkConfig = ArkNetworkConfig.Mutinynet;

// ── EF Core + SQLite via Bit.Besql (persistent via browser Cache API) ──
builder.Services.AddBesqlDbContextFactory<WalletDbContext>(options =>
{
    options.UseSqlite("Data Source=ArkadeWallet.db");
});
builder.Services.AddArkEfCoreStorage<WalletDbContext>();

// ── NArk SDK core services ──
builder.Services.AddArkCoreServices();
builder.Services.AddArkRestTransport(networkConfig);

// ── NArk SDK swap services ──
builder.Services.AddArkSwapServices();

// ── SDK infrastructure ──
builder.Services.AddSingleton<IIntentScheduler, SimpleIntentScheduler>();
builder.Services.AddSingleton<ISafetyService, WasmSafetyService>();
builder.Services.AddSingleton<IChainTimeProvider>(sp =>
{
    if (!string.IsNullOrWhiteSpace(networkConfig.ExplorerUri))
    {
        var baseUri = networkConfig.ExplorerUri.TrimEnd('/') + "/api/";
        return new EsploraChainTimeProvider(new Uri(baseUri));
    }
    return new FallbackChainTimeProvider();
});
builder.Services.AddSingleton<IWalletProvider, DefaultWalletProvider>();
builder.Services.AddSingleton<IAssetManager, AssetManager>();

// ── Wallet service (replaces gateway API client) ──
builder.Services.AddSingleton<ArkWalletService>();
builder.Services.AddSingleton<WalletState>();

var host = builder.Build();

// Create/migrate the SQLite database on first launch
var dbFactory = host.Services.GetRequiredService<IDbContextFactory<WalletDbContext>>();
await using var db = await dbFactory.CreateDbContextAsync();
await db.Database.EnsureCreatedAsync();

// Start SDK lifecycle services manually (WASM has no IHostedService support)
await host.Services.StartArkServicesAsync();

await host.RunAsync();
