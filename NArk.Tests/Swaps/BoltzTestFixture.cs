using System.Net;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NSubstitute;

namespace NArk.Tests;

/// <summary>
/// Shared construction for Boltz provider tests, which otherwise each re-roll
/// the same eleven-dependency <see cref="BoltzSwapProvider"/> wall.
/// </summary>
internal static class BoltzTestFixture
{
    public static readonly IOptions<BoltzClientOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new BoltzClientOptions
        {
            BoltzUrl = "https://example.test/",
            WebsocketUrl = "wss://example.test/v2/ws",
        });

    public static BoltzClient CreateClient(HttpMessageHandler? handler = null) =>
        new(new HttpClient(handler ?? new StubHandler()), Options);

    public static BoltzSwapProvider CreateProvider(
        BoltzClient? boltzClient = null,
        IClientTransport? clientTransport = null,
        IVtxoStorage? vtxoStorage = null,
        ISwapStorage? swapStorage = null) =>
        new(
            boltzClient ?? CreateClient(),
            new BoltzLimitsValidator(new CachedBoltzClient(new HttpClient(new StubHandler()), Options)),
            clientTransport ?? Substitute.For<IClientTransport>(),
            vtxoStorage ?? Substitute.For<IVtxoStorage>(),
            Substitute.For<IWalletProvider>(),
            swapStorage ?? Substitute.For<ISwapStorage>(),
            Substitute.For<IContractService>(),
            Substitute.For<IContractStorage>(),
            Substitute.For<ISafetyService>(),
            Substitute.For<IIntentStorage>(),
            Substitute.For<IBitcoinBlockchain>());

    /// <summary>Answers every request with 200 OK unless a responder is supplied.</summary>
    public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
    }
}
