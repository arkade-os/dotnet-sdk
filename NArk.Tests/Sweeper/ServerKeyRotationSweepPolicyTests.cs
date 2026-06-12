using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NArk.Core.Sweeper;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class ServerKeyRotationSweepPolicyTests
{
    private static readonly ECXOnlyPubKey ActiveKey = NewKey();
    private static readonly ECXOnlyPubKey DeprecatedKeyA = NewKey();
    private static readonly ECXOnlyPubKey DeprecatedKeyB = NewKey();
    private static readonly ECXOnlyPubKey UnknownKey = NewKey();

    private static long FutureCutoff() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3_600;
    private static long PastCutoff() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10;

    [Test]
    public async Task Does_not_sweep_coin_under_current_signer()
    {
        var policy = MakePolicy(new Dictionary<ECXOnlyPubKey, long> { { DeprecatedKeyA, FutureCutoff() } });
        var coin = MakeCoin(MakeDescriptor(ActiveKey));

        var result = await CollectAsync(policy.SweepAsync([coin]));

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Sweeps_coin_under_deprecated_signer_with_future_cutoff()
    {
        var policy = MakePolicy(new Dictionary<ECXOnlyPubKey, long> { { DeprecatedKeyA, FutureCutoff() } });
        var coin = MakeCoin(MakeDescriptor(DeprecatedKeyA));

        var result = await CollectAsync(policy.SweepAsync([coin]));

        Assert.That(result, Is.EqualTo(new[] { coin }));
    }

    [Test]
    public async Task Sweeps_coin_under_deprecated_signer_with_no_cutoff()
    {
        var policy = MakePolicy(new Dictionary<ECXOnlyPubKey, long> { { DeprecatedKeyA, 0 } });
        var coin = MakeCoin(MakeDescriptor(DeprecatedKeyA));
        var result = await CollectAsync(policy.SweepAsync([coin]));
        Assert.That(result, Is.EqualTo(new[] { coin }));
    }

    [Test]
    public async Task Does_not_sweep_coin_under_deprecated_signer_past_cutoff()
    {
        var policy = MakePolicy(new Dictionary<ECXOnlyPubKey, long> { { DeprecatedKeyA, PastCutoff() } });
        var coin = MakeCoin(MakeDescriptor(DeprecatedKeyA));

        var result = await CollectAsync(policy.SweepAsync([coin]));

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Does_not_sweep_coin_under_unknown_signer()
    {
        var policy = MakePolicy(new Dictionary<ECXOnlyPubKey, long> { { DeprecatedKeyA, FutureCutoff() } });
        var coin = MakeCoin(MakeDescriptor(UnknownKey));

        var result = await CollectAsync(policy.SweepAsync([coin]));

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Does_not_sweep_coin_with_null_signer_descriptor()
    {
        var policy = MakePolicy(new Dictionary<ECXOnlyPubKey, long> { { DeprecatedKeyA, FutureCutoff() } });
        var coin = MakeCoin(signerDescriptor: null);

        var result = await CollectAsync(policy.SweepAsync([coin]));

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Sweeps_only_migratable_coins_from_mixed_deprecated_signers()
    {
        // DeprecatedKeyA: future cutoff (MIGRATABLE) → sweep
        // DeprecatedKeyB: past cutoff (EXPIRED) → skip
        var policy = MakePolicy(new Dictionary<ECXOnlyPubKey, long>
        {
            { DeprecatedKeyA, FutureCutoff() },
            { DeprecatedKeyB, PastCutoff() },
        });
        var migratable = MakeCoin(MakeDescriptor(DeprecatedKeyA));
        var expired = MakeCoin(MakeDescriptor(DeprecatedKeyB));
        var current = MakeCoin(MakeDescriptor(ActiveKey));

        var result = await CollectAsync(policy.SweepAsync([migratable, expired, current]));

        Assert.That(result, Is.EqualTo(new[] { migratable }));
    }

    [Test]
    public async Task Sweeps_coins_under_all_due_now_deprecated_signers()
    {
        var policy = MakePolicy(new Dictionary<ECXOnlyPubKey, long>
        {
            { DeprecatedKeyA, 0 },
            { DeprecatedKeyB, 0 },
        });
        var coinA = MakeCoin(MakeDescriptor(DeprecatedKeyA));
        var coinB = MakeCoin(MakeDescriptor(DeprecatedKeyB));
        var result = await CollectAsync(policy.SweepAsync([coinA, coinB]));
        Assert.That(result, Is.EquivalentTo(new[] { coinA, coinB }));
    }

    [Test]
    public async Task Returns_empty_when_no_coins_provided()
    {
        var policy = MakePolicy(new Dictionary<ECXOnlyPubKey, long> { { DeprecatedKeyA, FutureCutoff() } });

        var result = await CollectAsync(policy.SweepAsync([]));

        Assert.That(result, Is.Empty);
    }

    private static ECXOnlyPubKey NewKey()
        => ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());

    private static OutputDescriptor MakeDescriptor(ECXOnlyPubKey key)
        => key.ToOutputDescriptor(Network.RegTest);

    private static ServerKeyRotationSweepPolicy MakePolicy(Dictionary<ECXOnlyPubKey, long> deprecated)
    {
        var emptyMultisig = new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());
        var serverInfo = new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: MakeDescriptor(ActiveKey),
            DeprecatedSigners: deprecated,
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: ActiveKey,
            CheckpointTapScript: new UnilateralPathArkTapScript(new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: "server-digest-abc");

        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(serverInfo);
        return new ServerKeyRotationSweepPolicy(transport);
    }

    private static ArkCoin MakeCoin(OutputDescriptor? signerDescriptor)
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var contract = new GenericArkContract(MakeDescriptor(ActiveKey), [script]);
        return new ArkCoin(
            walletIdentifier: "test-wallet",
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.One, 0),
            txOut: new TxOut(Money.Satoshis(100_000), Script.Empty),
            signerDescriptor: signerDescriptor,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false);
    }

    private static async Task<List<ArkCoin>> CollectAsync(IAsyncEnumerable<ArkCoin> source)
    {
        var result = new List<ArkCoin>();
        await foreach (var coin in source)
            result.Add(coin);
        return result;
    }
}
