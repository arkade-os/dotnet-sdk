using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NBitcoin;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

/// <summary>
/// Tests for <see cref="CovenantClaimRenewalService"/>.
/// </summary>
/// <remarks>
/// The service exists because a claim authorisation expires, so what matters is that it
/// renews the swaps that still need cover and leaves everything else alone — including
/// when the wallet has no covenant claims at all, which is the default configuration.
/// </remarks>
[TestFixture]
public class CovenantClaimRenewalServiceTests
{
    private const string ServerKeyHex =
        "035c9b445a18f7b189d33cd2d51a919f5db6ed91bd769493bee4214c810a0912ca";

    private const string SenderKeyHex =
        "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4";

    private const string ReceiverKeyHex =
        "021e1bb85455fe3f5aed60d101aa4dbdb9e7714f6226769a97a17a5331dadcd53b";

    private static readonly TaprootPubKey CovenantKey =
        new(Convert.FromHexString(
            "77a2e768588b5ced39c389e2ce803041bf9a70d503b34b49edf5970d912dcbb1"));

    private static readonly byte[] Preimage = Convert.FromHexString(
        "7c0337ab60da79ab83f02d2ac3cb0cbc72e820e3aea549030b09e29692639103");

    private static VHTLCContract BuildContract(bool withCovenant, bool withPreimage = true)
    {
        var server = KeyExtensions.ParseOutputDescriptor(ServerKeyHex, Network.RegTest);
        var sender = KeyExtensions.ParseOutputDescriptor(SenderKeyHex, Network.RegTest);
        var receiver = KeyExtensions.ParseOutputDescriptor(ReceiverKeyHex, Network.RegTest);
        var key = withCovenant ? CovenantKey : null;

        return withPreimage
            ? new VHTLCContract(server, sender, receiver, Preimage, new LockTime(265),
                new Sequence(144), new Sequence(144), new Sequence(144), key)
            : new VHTLCContract(server, sender, receiver,
                new uint160(NBitcoin.Crypto.Hashes.Hash160(Preimage).ToBytes(false)),
                new LockTime(265),
                new Sequence(144), new Sequence(144), new Sequence(144), key);
    }

    private static ArkSwap SwapFor(VHTLCContract contract, ArkSwapStatus status = ArkSwapStatus.Pending) =>
        new("swap-1", "wallet-1", ArkSwapType.ReverseSubmarine, "invoice", 50_000,
            contract.GetArkAddress().ScriptPubKey.ToHex(),
            contract.GetArkAddress().ToString(false),
            status, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            contract.Hash.ToString());

    private static (CovenantClaimRenewalService service, ICovenantClaimProvider provider) Build(
        VHTLCContract? contract, ArkSwapStatus status = ArkSwapStatus.Pending,
        ICovenantClaimProvider? provider = null)
    {
        var swapStorage = Substitute.For<ISwapStorage>();
        var contractStorage = Substitute.For<IContractStorage>();
        var transport = Substitute.For<IClientTransport>();

        provider ??= Substitute.For<ICovenantClaimProvider>();
        provider.RegistrationLifetime.Returns(TimeSpan.FromMinutes(15));

        var server = KeyExtensions.ParseOutputDescriptor(ServerKeyHex, Network.RegTest);
        transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(
            new ArkServerInfo(
                Dust: Money.Satoshis(1),
                SignerKey: server,
                DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(),
                Network: Network.RegTest,
                UnilateralExit: new Sequence(144),
                BoardingExit: new Sequence(144),
                ForfeitAddress: null!,
                ForfeitPubKey: null!,
                CheckpointTapScript: null!,
                FeeTerms: null!,
                Digest: ""));

        if (contract is null)
        {
            swapStorage.GetSwaps(cancellationToken: Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs(Array.Empty<ArkSwap>());
        }
        else
        {
            swapStorage.GetSwaps(cancellationToken: Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs([SwapFor(contract, status)]);
            contractStorage.GetContracts(cancellationToken: Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs([contract.ToEntity("wallet-1", null, null, ContractActivityState.Active)]);
        }

        return (new CovenantClaimRenewalService(swapStorage, contractStorage, transport, provider), provider);
    }

    [Test]
    public async Task RenewsPendingSwapWithCovenantLeaf()
    {
        var contract = BuildContract(withCovenant: true);
        var (service, provider) = Build(contract);

        await service.RenewAllAsync(CancellationToken.None);

        // Matched by content: the service hands over the preimage from the contract it
        // reloaded from storage, which is an equal but distinct array.
        await provider.Received(1).RegisterAsync(
            contract.GetArkAddress().ToString(false),
            Arg.Is<byte[]>(p => p.SequenceEqual(Preimage)),
            Arg.Is<Script>(s => s == BuildExpectedClaimDestination()),
            Arg.Is<TapScript[]>(t => t.Length == 7),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The destination the covenant leaf commits to: a payment contract on the VHTLC's
    /// own receiver descriptor, matching what a normal sweep would recycle into.
    /// </summary>
    private static Script BuildExpectedClaimDestination()
    {
        var server = KeyExtensions.ParseOutputDescriptor(ServerKeyHex, Network.RegTest);
        var receiver = KeyExtensions.ParseOutputDescriptor(ReceiverKeyHex, Network.RegTest);
        return new ArkPaymentContract(server, new Sequence(144), receiver)
            .GetArkAddress().ScriptPubKey;
    }

    /// <summary>
    /// A plain VHTLC has no covenant path, so renewing it would register an
    /// authorisation the signer could never act on.
    /// </summary>
    [Test]
    public async Task SkipsSwapWithoutCovenantLeaf()
    {
        var (service, provider) = Build(BuildContract(withCovenant: false));

        await service.RenewAllAsync(CancellationToken.None);

        await provider.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default!, default!, default!);
    }

    /// <summary>
    /// Restored or watch-only wallets can hold the contract without the secret; there is
    /// nothing to hand the signer, so the swap is left to the wallet's own claim path.
    /// </summary>
    [Test]
    public async Task SkipsSwapWithoutPreimage()
    {
        var (service, provider) = Build(BuildContract(withCovenant: true, withPreimage: false));

        await service.RenewAllAsync(CancellationToken.None);

        await provider.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default!, default!, default!);
    }

    [Test]
    public async Task DoesNothingWhenNoPendingSwaps()
    {
        var (service, provider) = Build(contract: null);

        await service.RenewAllAsync(CancellationToken.None);

        await provider.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default!, default!, default!);
    }

    /// <summary>
    /// One failing swap must not stop the rest of the pass — otherwise a single bad
    /// registration would silently end coverage for every other pending swap.
    /// </summary>
    [Test]
    public void OneFailingSwapDoesNotAbortThePass()
    {
        var provider = Substitute.For<ICovenantClaimProvider>();
        provider.RegisterAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(Task.FromException(new HttpRequestException("daemon down")));

        var (service, _) = Build(BuildContract(withCovenant: true), provider: provider);

        Assert.DoesNotThrowAsync(() => service.RenewAllAsync(CancellationToken.None));
    }

    /// <summary>
    /// Half the advertised lifetime, so one missed pass still leaves the authorisation
    /// valid rather than opening a gap.
    /// </summary>
    [Test]
    public void RenewalIntervalIsHalfTheRegistrationLifetime()
    {
        var (service, _) = Build(BuildContract(withCovenant: true));

        Assert.That(service.RenewalInterval, Is.EqualTo(TimeSpan.FromMinutes(7.5)));
    }

    /// <summary>
    /// The default configuration has no provider, and the service is wired
    /// unconditionally — so starting it must be a no-op rather than a crash.
    /// </summary>
    [Test]
    public async Task WithoutProvider_StartIsANoOp()
    {
        var service = new CovenantClaimRenewalService(
            Substitute.For<ISwapStorage>(),
            Substitute.For<IContractStorage>(),
            Substitute.For<IClientTransport>());

        Assert.DoesNotThrowAsync(() => service.StartAsync(CancellationToken.None));
        await service.RenewAllAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
