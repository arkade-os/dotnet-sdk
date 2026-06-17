using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NArk.Delegator;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin.Scripting;

namespace NArk.Tests.End2End.DelegatorServer;

/// <summary>
/// Hosts NArk.Delegator in-process over Kestrel against the real regtest arkd, backed by a funded
/// delegator wallet. Cleartext can't ALPN-negotiate, so gRPC (HTTP/2) and REST (HTTP/1.1) are split
/// onto two ports: <see cref="BaseUrl"/> for gRPC and <see cref="RestBaseUrl"/> for REST. Disposing
/// stops the host.
/// </summary>
internal sealed class InProcessDelegatorHost : IAsyncDisposable
{
    public required string BaseUrl { get; init; }
    public required string RestBaseUrl { get; init; }
    public required WebApplication App { get; init; }
    public required string DelegatorWalletId { get; init; }
    public required InMemoryWalletProvider WalletProvider { get; init; }
    public required OutputDescriptor DelegateDescriptor { get; init; }
    public required IIntentStorage IntentStorage { get; init; }
    public required IVtxoStorage WalletVtxoStorage { get; init; }

    public static async Task<InProcessDelegatorHost> StartAsync(string fee = "0", string delegatorAddress = "")
    {
        // A funded delegator wallet: pays batch fees and (later) receives the service fee.
        var w = await FundedWalletHelper.GetFundedWallet(vtxoCount: 1, amountSatsPerVtxo: 1_000_000);
        var delegateDescriptor = await (await w.walletProvider.GetAddressProviderAsync(w.walletIdentifier))!
            .GetNextSigningDescriptor();
        var intentStorage = TestStorage.CreateIntentStorage();

        var (grpcPort, restPort) = FreePorts();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Cleartext gRPC needs an HTTP/2-only endpoint (no ALPN to negotiate); REST transcoding is HTTP/1.1.
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.ListenLocalhost(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
            o.ListenLocalhost(restPort, lo => lo.Protocols = HttpProtocols.Http1);
        });

        builder.Services.AddSingleton<IWalletProvider>(w.walletProvider);
        builder.Services.AddSingleton<IClientTransport>(w.clientTransport);
        builder.Services.AddSingleton<IIntentStorage>(intentStorage);
        builder.Services.AddSingleton<IContractStorage>(w.contracts);
        builder.Services.AddSingleton<IVtxoStorage>(w.vtxoStorage);
        builder.Services.AddNArkDelegator(o =>
        {
            o.WalletId = w.walletIdentifier;
            o.DelegateDescriptor = delegateDescriptor;
            o.Fee = fee;
            o.DelegatorAddress = delegatorAddress;
        });

        var app = builder.Build();
        app.MapNArkDelegator();
        await app.StartAsync();

        return new InProcessDelegatorHost
        {
            BaseUrl = $"http://localhost:{grpcPort}",
            RestBaseUrl = $"http://localhost:{restPort}",
            App = app,
            DelegatorWalletId = w.walletIdentifier,
            WalletProvider = w.walletProvider,
            DelegateDescriptor = delegateDescriptor,
            IntentStorage = intentStorage,
            WalletVtxoStorage = w.vtxoStorage
        };
    }

    private static (int grpcPort, int restPort) FreePorts()
    {
        // Hold both listeners open simultaneously so the two ports can't collide.
        var l1 = new TcpListener(IPAddress.Loopback, 0);
        var l2 = new TcpListener(IPAddress.Loopback, 0);
        l1.Start();
        l2.Start();
        try { return (((IPEndPoint)l1.LocalEndpoint).Port, ((IPEndPoint)l2.LocalEndpoint).Port); }
        finally { l1.Stop(); l2.Stop(); }
    }

    public async ValueTask DisposeAsync() => await App.DisposeAsync();
}
