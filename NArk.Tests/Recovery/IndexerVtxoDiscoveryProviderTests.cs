using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Recovery;
using NArk.Core.Scripts;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests.Recovery;

/// <summary>
/// Contract for the legacy-aware indexer discovery: a payment VTXO locked under a
/// <b>deprecated</b> server signer (server-key rotation) must still be discovered,
/// returning the contract built from that deprecated signer — not the current one.
/// </summary>
[TestFixture]
public class IndexerVtxoDiscoveryProviderTests
{
    private static readonly Network Net = Network.RegTest;

    private static OutputDescriptor Desc() =>
        KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Net);

    private static ArkServerInfo ServerInfo(OutputDescriptor signer, params ECXOnlyPubKey[] deprecated) =>
        new(
            Dust: Money.Satoshis(330),
            SignerKey: signer,
            DeprecatedSigners: deprecated.ToDictionary(k => k, _ => 1_700_000_000L),
            Network: Net,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Net),
            ForfeitPubKey: signer.Extract().XOnlyPubKey,
            CheckpointTapScript: new UnilateralPathArkTapScript(
                new Sequence(144), new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>())),
            FeeTerms: new ArkOperatorFeeTerms("0", "0", "0", "0", "0"));

    private static async IAsyncEnumerable<ArkVtxo> Stream(params string[] scripts)
    {
        foreach (var script in scripts)
            yield return new ArkVtxo(
                script, new string('0', 62) + "aa", 0, 1000, null, null, false,
                DateTimeOffset.UtcNow, null, null);
        await Task.CompletedTask;
    }

    private static (IClientTransport transport, IndexerVtxoDiscoveryProvider sut) Build(
        ArkServerInfo serverInfo, params string[] hitScripts)
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(serverInfo));
        transport.GetVtxoByScriptsAsSnapshot(Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream(hitScripts));
        return (transport, new IndexerVtxoDiscoveryProvider(transport));
    }

    private static ArkWalletInfo HdWallet(OutputDescriptor account) =>
        new("w1", "secret", null, WalletType.HD, account.ToString(), 0);

    [Test]
    public async Task Discovers_vtxo_under_deprecated_signer_legacy_script()
    {
        var current = Desc();
        var user = Desc();
        var deprecatedSigner = Desc().Extract().XOnlyPubKey;
        var serverInfo = ServerInfo(current, deprecatedSigner);

        // The legacy script: a payment contract under the DEPRECATED signer (the
        // exit delay is invariant; only the server key differs). Reconstruct the
        // signer descriptor the same way the provider does — tr(<32-byte x-only>).
        var deprecatedSignerDesc = deprecatedSigner.ToOutputDescriptor(Net);
        var legacyScript = new ArkPaymentContract(deprecatedSignerDesc, serverInfo.UnilateralExit, user)
            .GetScriptPubKey().ToHex();
        var currentScript = new ArkPaymentContract(current, serverInfo.UnilateralExit, user)
            .GetScriptPubKey().ToHex();
        Assert.That(legacyScript, Is.Not.EqualTo(currentScript), "legacy and current scripts must differ");

        var (_, sut) = Build(serverInfo, legacyScript);

        var result = await sut.DiscoverAsync(HdWallet(current), user, index: 0);

        Assert.That(result.Used, Is.True);
        Assert.That(result.Contracts, Has.Count.EqualTo(1));
        // The discovered contract must be the LEGACY one — the whole point.
        Assert.That(result.Contracts[0].GetScriptPubKey().ToHex(), Is.EqualTo(legacyScript));
    }

    [Test]
    public async Task Discovers_current_signer_vtxo()
    {
        var current = Desc();
        var user = Desc();
        var serverInfo = ServerInfo(current);
        var currentScript = new ArkPaymentContract(current, serverInfo.UnilateralExit, user)
            .GetScriptPubKey().ToHex();

        var (_, sut) = Build(serverInfo, currentScript);

        var result = await sut.DiscoverAsync(HdWallet(current), user, index: 0);

        Assert.That(result.Used, Is.True);
        Assert.That(result.Contracts[0].GetScriptPubKey().ToHex(), Is.EqualTo(currentScript));
    }

    [Test]
    public async Task Discovers_vtxo_under_mainnet_legacy_exit_delay()
    {
        // arkd advertises a SHORTER current delay (144 blocks); the wallet was
        // minted while mainnet still ran the original 7-day delay. Without the
        // legacy candidate the discovery would miss those VTXOs entirely.
        var current = Desc();
        var user = Desc();
        var serverInfo = new ArkServerInfo(
            Dust: Money.Satoshis(330),
            SignerKey: current,
            DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(),
            Network: Network.Main,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", Network.Main),
            ForfeitPubKey: current.Extract().XOnlyPubKey,
            CheckpointTapScript: new UnilateralPathArkTapScript(
                new Sequence(144), new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>())),
            FeeTerms: new ArkOperatorFeeTerms("0", "0", "0", "0", "0"));

        // MAINNET_UNILATERAL_EXIT_DELAY from arkade-os/ts-sdk: 605184 s (~7 days).
        var legacyDelay = new Sequence(TimeSpan.FromSeconds(605184));
        var legacyScript = new ArkPaymentContract(current, legacyDelay, user)
            .GetScriptPubKey().ToHex();
        var currentScript = new ArkPaymentContract(current, serverInfo.UnilateralExit, user)
            .GetScriptPubKey().ToHex();
        Assert.That(legacyScript, Is.Not.EqualTo(currentScript),
            "legacy 7-day script must differ from the current-delay script");

        var (_, sut) = Build(serverInfo, legacyScript);

        var result = await sut.DiscoverAsync(HdWallet(current), user, index: 0);

        Assert.That(result.Used, Is.True);
        Assert.That(result.Contracts, Has.Count.EqualTo(1));
        Assert.That(result.Contracts[0].GetScriptPubKey().ToHex(), Is.EqualTo(legacyScript));
    }

    [Test]
    public async Task No_vtxo_returns_NotFound()
    {
        var current = Desc();
        var user = Desc();
        var (_, sut) = Build(ServerInfo(current) /* no hit scripts */);

        var result = await sut.DiscoverAsync(HdWallet(current), user, index: 0);

        Assert.That(result.Used, Is.False);
        Assert.That(result.Contracts, Is.Empty);
    }
}
