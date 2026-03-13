using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Blockchain.NBXplorer;
using NArk.Core.Services;
using NArk.Core.Wallet;
using NArk.Hosting;
using NArk.Storage.EfCore.Hosting;
using NArk.Swaps.Hosting;
using NArk.Wallet.Client;
using NArk.Wallet.Client.Services;
using SqliteWasmBlazor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ── Network ──
var networkConfig = ArkNetworkConfig.Mutinynet;

// ── EF Core + SQLite WASM (persistent via OPFS) ──
builder.Services.AddDbContextFactory<WalletDbContext>(options =>
{
    var connection = new SqliteWasmConnection("Data Source=ArkadeWallet.db");
    options.UseSqliteWasm(connection);
});
builder.Services.AddArkEfCoreStorage<WalletDbContext>();

// ── NArk SDK core services ──
builder.Services.AddArkCoreServices();
builder.Services.AddArkRestTransport(networkConfig);

// ── NArk SDK swap services ──
builder.Services.AddArkSwapServices();

// ── SDK infrastructure ──
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

// Initialize SQLite WASM (Web Worker + OPFS) and run migrations
await host.Services.InitializeSqliteWasmDatabaseAsync<WalletDbContext>();

// Start SDK lifecycle services manually (WASM has no IHostedService support)
await host.Services.StartArkServicesAsync();

await host.RunAsync();
