using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Core;
using NArk.Core.Extensions;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

/// <summary>
/// Covers how <see cref="ArkadeCashService.ClaimAsync"/> triages the VTXOs at a note's address.
/// The sweep itself needs a live Arkade server and is covered end-to-end; what is pinned here is
/// that nothing unsweepable is ever handed to the sweep, and that each exclusion is reported with
/// the reason a caller would act on.
/// </summary>
public class ArkadeCashClaimTests
{
    private static readonly ECPrivKey ServerPrivkey = ECPrivKey.Create(
        Convert.FromHexString("a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0"));

    private const ulong Dust = 546;

    [Test]
    public async Task ReportsEveryUnsweepableVtxoWithItsReason()
    {
        using var cash = ArkadeCash.Generate(ServerPrivkey.CreateXOnlyPubKey(), new Sequence(144), "tarkadecash");
        var script = cash.GetAddress(Network.RegTest).ScriptPubKey.ToHex();

        var transport = CreateTransport(cash, [
            Vtxo(script, 0, 50_000, spentBy: "aa".PadLeft(64, '0')),
            Vtxo(script, 1, 50_000, swept: true),
            Vtxo(script, 2, Dust - 1),
            Vtxo(script, 3, 50_000, assets: [new VtxoAsset("asset-1", 5)]),
        ]);

        var result = await new ArkadeCashService(transport, Substitute.For<Abstractions.Safety.ISafetyService>(),
            Substitute.For<Abstractions.Intents.IIntentStorage>()).ClaimAsync(cash, cash.GetAddress(Network.RegTest));

        // Nothing was sweepable, so the sweep never ran — the whole set comes back as a report.
        Assert.That(result.Swept, Is.EqualTo(0UL));
        Assert.That(result.UnclaimedAmount, Is.EqualTo(50_000UL + 50_000 + (Dust - 1) + 50_000));
        Assert.That(result.Unclaimed.Select(v => v.Reason), Is.EquivalentTo(new[]
        {
            ArkadeCashUnclaimedReason.AlreadySpent,
            ArkadeCashUnclaimedReason.ServerSwept,
            ArkadeCashUnclaimedReason.Subdust,
            ArkadeCashUnclaimedReason.AssetBearing,
        }));
    }

    [Test]
    public async Task ReportsNothingWhenTheNoteAddressIsEmpty()
    {
        using var cash = ArkadeCash.Generate(ServerPrivkey.CreateXOnlyPubKey(), new Sequence(144), "tarkadecash");
        var transport = CreateTransport(cash, []);

        var result = await new ArkadeCashService(transport, Substitute.For<Abstractions.Safety.ISafetyService>(),
            Substitute.For<Abstractions.Intents.IIntentStorage>()).ClaimAsync(cash, cash.GetAddress(Network.RegTest));

        Assert.That(result.Swept, Is.EqualTo(0UL));
        Assert.That(result.Unclaimed, Is.Empty);
        Assert.That(result.UnclaimedAmount, Is.EqualTo(0UL));
    }

    [Test]
    public async Task QueriesTheScriptTheNoteEncodes_NotTheServersCurrentSigner()
    {
        // A note is spent under the signer it was issued against. If the claim looked the funds up
        // under whatever key the server currently advertises, a note funded before a signer rotation
        // would come back empty.
        using var cash = ArkadeCash.Generate(ServerPrivkey.CreateXOnlyPubKey(), new Sequence(144), "tarkadecash");
        var rotatedSigner = ECPrivKey.Create(Convert.FromHexString(
            "b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c3c4c5c6c7c8c9cacbcccdcecfd0"));

        var transport = CreateTransport(cash, [], currentSigner: rotatedSigner.CreateXOnlyPubKey());
        await new ArkadeCashService(transport, Substitute.For<Abstractions.Safety.ISafetyService>(),
            Substitute.For<Abstractions.Intents.IIntentStorage>()).ClaimAsync(cash, cash.GetAddress(Network.RegTest));

        var noteScript = cash.GetAddress(Network.RegTest).ScriptPubKey.ToHex();
        transport.Received(1).GetVtxoByScriptsAsSnapshot(
            Arg.Is<IReadOnlySet<string>>(s => s.Count == 1 && s.Contains(noteScript)),
            Arg.Any<CancellationToken>());
    }

    private static IClientTransport CreateTransport(
        ArkadeCash cash, ArkVtxo[] vtxos, ECXOnlyPubKey? currentSigner = null)
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(CreateServerInfo(currentSigner ?? cash.ServerPubkey));
        transport.GetVtxoByScriptsAsSnapshot(Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(vtxos));
        return transport;
    }

    private static async IAsyncEnumerable<ArkVtxo> ToAsyncEnumerable(ArkVtxo[] vtxos)
    {
        foreach (var vtxo in vtxos)
        {
            yield return vtxo;
        }
        await Task.CompletedTask;
    }

    private static ArkVtxo Vtxo(
        string script, uint vout, ulong amount, string? spentBy = null, bool swept = false,
        IReadOnlyList<VtxoAsset>? assets = null) =>
        new(script, new uint256((ulong)(vout + 1)).ToString(), vout, amount, spentBy, null, swept,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), null, Assets: assets);

    private static ArkServerInfo CreateServerInfo(ECXOnlyPubKey signerKey)
    {
        var emptyMultisig = new Core.Scripts.NofNMultisigTapScript([]);
        return new ArkServerInfo(
            Dust: Money.Satoshis(Dust),
            SignerKey: OutputDescriptor.Parse($"tr({Convert.ToHexString(signerKey.ToBytes()).ToLowerInvariant()})", Network.RegTest),
            DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance),
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
            CheckpointTapScript: new Core.Scripts.UnilateralPathArkTapScript(new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: "");
    }

    [OneTimeTearDown]
    public void Cleanup() => ServerPrivkey.Dispose();
}
