using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Arkade.Emulator;
using NArk.Arkade.Scripts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Arkade;

/// <summary>
/// Covers <see cref="ArkadeEmulatorPacketProvider"/> — the <see cref="ISpendExtensionPacketProvider"/>
/// SpendingService plugs in to contribute the emulator OP_RETURN packet for any Arkade-bound input.
/// Complements <c>ArkadePsbtExtensionsTests</c> (which tests the underlying <c>BuildEmulatorPackets</c>)
/// by asserting the provider surface itself picks Arkade coins and skips plain ones.
/// </summary>
[TestFixture]
public class ArkadeEmulatorPacketProviderTests
{
    private readonly ArkadeEmulatorPacketProvider _provider = new();

    [Test]
    public void BuildPackets_EmitsOneEmulatorPacket_ForArkadeCoin()
    {
        var (alice, bob, emulator) = MakeKeys();
        var arkade = new ArkadeNofNMultisigTapScript([0xc4], [alice], [emulator]);

        var packets = _provider.BuildPackets([MakeCoin(alice, bob, arkade)]);

        Assert.That(packets, Has.Count.EqualTo(1));
        Assert.That(packets[0], Is.InstanceOf<EmulatorPacket>());
    }

    [Test]
    public void BuildPackets_Empty_ForPlainCoin()
    {
        var (alice, bob, _) = MakeKeys();
        var plain = new NofNMultisigTapScript([alice, bob]);

        Assert.That(_provider.BuildPackets([MakeCoin(alice, bob, plain)]), Is.Empty);
    }

    [Test]
    public void BuildPackets_TargetsOnlyTheArkadeVin_InAMixedSpend()
    {
        var (alice, bob, emulator) = MakeKeys();
        var plain = new NofNMultisigTapScript([alice, bob]);
        var arkade = new ArkadeNofNMultisigTapScript([0xc4], [alice], [emulator]);

        // vin 0 = plain, vin 1 = arkade.
        var packets = _provider.BuildPackets([MakeCoin(alice, bob, plain), MakeCoin(alice, bob, arkade)]);

        var packet = packets[0] as EmulatorPacket;
        Assert.That(packet, Is.Not.Null);
        Assert.That(packet!.Entries, Has.Count.EqualTo(1));
        Assert.That(packet.Entries[0].Vin, Is.EqualTo((ushort)1));
    }

    // ─── Helpers (mirror ArkadePsbtExtensionsTests) ───────────────────

    private static (ECXOnlyPubKey alice, ECXOnlyPubKey bob, TaprootPubKey emulator) MakeKeys()
    {
        var rng = new Random(11);
        ECXOnlyPubKey Make()
        {
            var seed = new byte[32];
            rng.NextBytes(seed);
            return ECXOnlyPubKey.Create(new Key(seed).PubKey.TaprootInternalKey.ToBytes());
        }
        var introSeed = new byte[32];
        rng.NextBytes(introSeed);
        var emulator = new Key(introSeed).PubKey.GetTaprootFullPubKey().OutputKey;
        return (Make(), Make(), emulator);
    }

    private static ArkCoin MakeCoin(ECXOnlyPubKey alice, ECXOnlyPubKey bob, ScriptBuilder spendingBuilder)
    {
        var contract = new TestContract(alice, spendingBuilder);
        var prevOut = new TxOut(Money.Coins(1), contract.GetScriptPubKey());
        return new ArkCoin(
            walletIdentifier: "test",
            contract: contract,
            birth: DateTimeOffset.UnixEpoch,
            expiresAt: null,
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.Zero, 0),
            txOut: prevOut,
            signerDescriptor: null,
            spendingScriptBuilder: spendingBuilder,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false);
    }

    private sealed class TestContract : ArkContract
    {
        private readonly ScriptBuilder _spending;

        public TestContract(ECXOnlyPubKey serverKey, ScriptBuilder spending)
            : base(OutputDescriptor.Parse(
                $"rawtr({Convert.ToHexString(serverKey.ToBytes()).ToLowerInvariant()})", Network.RegTest))
            => _spending = spending;

        public override string Type => "test";
        public override ContractScope DefaultScope => ContractScope.Offchain;
        protected override IEnumerable<ScriptBuilder> GetScriptBuilders() { yield return _spending; }
        protected override Dictionary<string, string> GetContractData() => new() { ["arkcontract"] = Type };
    }
}
