using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Services;
using NArk.Tests.Common;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests;

/// <summary>
/// Pins the cross-SDK deterministic-preimage message format produced by
/// <see cref="SwapsManagementService.BuildPreimageMessage"/>:
/// <c>PreimageTag || x-only pubkey (32B) || u32 LE index</c>.
///
/// Anchoring on the canonical x-only public key — not the descriptor's
/// non-canonical <c>.ToString()</c> — is what lets a restored wallet, which
/// rediscovers the swap via a <em>reconstructed</em> bare receiver descriptor,
/// re-derive the same preimage the original (HD signing) descriptor produced.
/// </summary>
[TestFixture]
public class PreimageDerivationTests
{
    private const string Tag = "Arkade-Boltz-Preimage-v1";
    private static readonly Mnemonic AbandonMnemonic =
        new("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");

    [Test]
    public void Message_IsTagPlusXOnlyPubkeyPlusIndexLittleEndian()
    {
        var key = new Key();
        var descriptor = OutputDescriptor.Parse($"tr({key.PubKey.ToHex()})", Network.Main);
        var xOnly = descriptor.Extract().XOnlyPubKey.ToBytes(); // 32 bytes

        var message = SwapsManagementService.BuildPreimageMessage(descriptor, index: 0);

        var expected = Encoding.UTF8.GetBytes(Tag)
            .Concat(xOnly)
            .Concat(new byte[] { 0x00, 0x00, 0x00, 0x00 }) // u32 LE, index 0
            .ToArray();
        Assert.That(message, Is.EqualTo(expected));
    }

    [Test]
    public void Message_IndexEncodedAsUInt32LittleEndian()
    {
        var descriptor = OutputDescriptor.Parse($"tr({new Key().PubKey.ToHex()})", Network.Main);

        var message = SwapsManagementService.BuildPreimageMessage(descriptor, index: 1);

        Assert.That(message[^4..], Is.EqualTo(new byte[] { 0x01, 0x00, 0x00, 0x00 }));
    }

    [Test]
    public void Message_IsIndependentOfDescriptorStringForm()
    {
        var accountXpub = new ExtKey().Neuter().ToString(Network.Main);
        var hdDescriptor = OutputDescriptor.Parse(
            $"tr([d34db33f/86'/0'/0']{accountXpub}/0/*)", Network.Main);
        var derivedXOnly = hdDescriptor.Extract().XOnlyPubKey;
        var bareDescriptor = OutputDescriptor.Parse(
            $"tr({Convert.ToHexString(derivedXOnly.ToBytes()).ToLowerInvariant()})", Network.Main);

        Assert.That(hdDescriptor.ToString(), Is.Not.EqualTo(bareDescriptor.ToString()),
            "precondition: HD signing vs bare receiver descriptors serialise differently");

        Assert.That(
            SwapsManagementService.BuildPreimageMessage(hdDescriptor, 0),
            Is.EqualTo(SwapsManagementService.BuildPreimageMessage(bareDescriptor, 0)));
    }

    [Test]
    public async Task DerivePreimage_IsDeterministic()
    {
        var wallet = SimpleSeedWallet.CreateForSigning(AbandonMnemonic, Network.RegTest);
        var svc = MakeService(new MockedSigningWalletProvider(wallet));
        var descriptor = MakeDescriptor(AbandonMnemonic, Network.RegTest, index: 0);

        var first = await svc.DerivePreimageAsync("wallet", descriptor, 0, CancellationToken.None);
        var second = await svc.DerivePreimageAsync("wallet", descriptor, 0, CancellationToken.None);

        Assert.That(first, Has.Length.EqualTo(32));
        Assert.That(first, Is.EqualTo(second), "Same (wallet, descriptor, index) must always yield the same preimage");
    }

    [Test]
    public async Task DerivePreimage_DifferentKeyIndex_DifferentPreimage()
    {
        var wallet = SimpleSeedWallet.CreateForSigning(AbandonMnemonic, Network.RegTest);
        var svc = MakeService(new MockedSigningWalletProvider(wallet));

        var desc0 = MakeDescriptor(AbandonMnemonic, Network.RegTest, index: 0);
        var desc1 = MakeDescriptor(AbandonMnemonic, Network.RegTest, index: 1);

        var preimage0 = await svc.DerivePreimageAsync("wallet", desc0, 0, CancellationToken.None);
        var preimage1 = await svc.DerivePreimageAsync("wallet", desc1, 0, CancellationToken.None);

        Assert.That(preimage0, Is.Not.EqualTo(preimage1),
            "Descriptors at different key indices must produce different preimages");
    }

    [Test]
    public async Task DerivePreimage_DifferentPreimageIndex_DifferentPreimage()
    {
        var wallet = SimpleSeedWallet.CreateForSigning(AbandonMnemonic, Network.RegTest);
        var svc = MakeService(new MockedSigningWalletProvider(wallet));
        var descriptor = MakeDescriptor(AbandonMnemonic, Network.RegTest, index: 0);

        var preimage0 = await svc.DerivePreimageAsync("wallet", descriptor, 0, CancellationToken.None);
        var preimage1 = await svc.DerivePreimageAsync("wallet", descriptor, 1, CancellationToken.None);

        Assert.That(preimage0, Is.Not.EqualTo(preimage1),
            "Different preimage index must produce different preimage for the same key");
    }

    [Test]
    public async Task DerivePreimage_WatchOnlyWallet_ReturnsRandomBytes()
    {
        // GetSignerAsync returns null → watch-only path → random 32 bytes each call
        var watchOnly = Substitute.For<IWalletProvider>();
        watchOnly.GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IArkadeWalletSigner?>(null));
        var svc = MakeService(watchOnly);
        var descriptor = MakeDescriptor(AbandonMnemonic, Network.RegTest, index: 0);

        var first = await svc.DerivePreimageAsync("wallet", descriptor, 0, CancellationToken.None);
        var second = await svc.DerivePreimageAsync("wallet", descriptor, 0, CancellationToken.None);

        Assert.That(first, Has.Length.EqualTo(32));
        Assert.That(first, Is.Not.EqualTo(second),
            "Watch-only wallets get a fresh random preimage each call — they cannot recover");
    }

    /// <summary>
    /// Prints a cross-SDK test vector to <see cref="TestContext.Out"/>.
    /// Run once to capture the expected hex and pin it in other SDK test suites.
    /// </summary>
    [Test, Explicit("Generates a cross-SDK preimage test vector; run manually and hard-code the output")]
    public async Task PrintCrossSDKTestVector()
    {
        var wallet = SimpleSeedWallet.CreateForSigning(AbandonMnemonic, Network.RegTest);
        var svc = MakeService(new MockedSigningWalletProvider(wallet));
        var descriptor = MakeDescriptor(AbandonMnemonic, Network.RegTest, index: 0);

        var preimage = await svc.DerivePreimageAsync("wallet", descriptor, 0, CancellationToken.None);
        var preimageHex = Convert.ToHexString(preimage).ToLowerInvariant();

        TestContext.Out.WriteLine("=== Cross-SDK Preimage Test Vector ===");
        TestContext.Out.WriteLine($"mnemonic   : {AbandonMnemonic}");
        TestContext.Out.WriteLine($"network    : regtest (coinType=1)");
        TestContext.Out.WriteLine($"path       : m/86'/1'/0'/0/0");
        TestContext.Out.WriteLine($"descriptor : {descriptor}");
        TestContext.Out.WriteLine($"index      : 0");
        TestContext.Out.WriteLine($"preimage   : {preimageHex}");
        TestContext.Out.WriteLine($"hash       : {Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant()}");
    }

    /// <summary>
    /// Loads each entry from <c>Assets/Fixtures/preimage_vectors.json</c> and verifies
    /// that <see cref="SwapsManagementService.BuildPreimageMessage"/> and
    /// <see cref="SwapsManagementService.DerivePreimageAsync"/> produce the pinned values.
    /// If any case fails, the preimage scheme has changed in a cross-SDK-breaking way —
    /// re-generate the fixture by running ConsoleApp1 and committing the new file.
    /// </summary>
    [TestCaseSource(nameof(LoadVectorsFromFixture))]
    public async Task DerivePreimage_MatchesPinnedVector(
        string network, int derivationIndex,
        string expectedMessageHex, string expectedPreimageHex)
    {
        var net = network == "bitcoin" ? Network.Main : Network.RegTest;
        var wallet = SimpleSeedWallet.CreateForSigning(AbandonMnemonic, net);
        var svc = MakeService(new MockedSigningWalletProvider(wallet));
        var descriptor = MakeDescriptor(AbandonMnemonic, net, derivationIndex);

        var message = SwapsManagementService.BuildPreimageMessage(descriptor, index: 0);
        var preimage = await svc.DerivePreimageAsync("wallet", descriptor, 0, CancellationToken.None);

        Assert.That(Convert.ToHexString(message).ToLowerInvariant(), Is.EqualTo(expectedMessageHex));
        Assert.That(Convert.ToHexString(preimage).ToLowerInvariant(), Is.EqualTo(expectedPreimageHex));
    }

    private static IEnumerable<TestCaseData> LoadVectorsFromFixture()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Assets", "Fixtures", "preimage_vectors.json");
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

        foreach (var networkProp in root.GetProperty("vectors").EnumerateObject())
        {
            var networkName = networkProp.Name;
            foreach (var entry in networkProp.Value.EnumerateArray())
            {
                var derivationIndex = entry.GetProperty("derivationIndex").GetInt32();
                var msg = entry.GetProperty("expectedPreimageMessage").GetString()!;
                var pre = entry.GetProperty("expectedPreimage").GetString()!;
                yield return new TestCaseData(networkName, derivationIndex, msg, pre)
                    .SetName($"{networkName}_derivation{derivationIndex}");
            }
        }
    }

#region helpers
    private static OutputDescriptor MakeDescriptor(Mnemonic mnemonic, Network network, int index)
    {
        var extKey = mnemonic.DeriveExtKey();
        var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();
        var coinType = network.ChainName == ChainName.Mainnet ? "0" : "1";
        var accountXpriv = extKey.Derive(new KeyPath($"m/86'/{coinType}'/0'"));
        var accountXpub = accountXpriv.Neuter().GetWif(network).ToWif();
        return OutputDescriptor.Parse(
            $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/{index})", network);
    }

    private static SwapsManagementService MakeService(IWalletProvider walletProvider) =>
        new(
            providers: Array.Empty<ISwapProvider>(),
            spendingService: null!,
            clientTransport: null!,
            vtxoStorage: Substitute.For<IVtxoStorage>(),
            walletProvider: walletProvider,
            swapsStorage: Substitute.For<ISwapStorage>(),
            contractService: null!,
            contractStorage: null!,
            safetyService: null!,
            intentStorage: Substitute.For<IIntentStorage>(),
            chainTimeProvider: null!);

    private sealed class MockedSigningWalletProvider : IWalletProvider
    {
        private readonly SimpleSeedWallet _seedWallet;

        public MockedSigningWalletProvider(SimpleSeedWallet seedWallet) => _seedWallet = seedWallet;

        public Task<IArkadeWalletSigner?> GetSignerAsync(string identifier, CancellationToken cancellationToken = default)
            => Task.FromResult<IArkadeWalletSigner?>(_seedWallet);

        public Task<IArkadeAddressProvider?> GetAddressProviderAsync(string identifier, CancellationToken cancellationToken = default)
            => Task.FromResult<IArkadeAddressProvider?>(_seedWallet);
    }
#endregion
}
