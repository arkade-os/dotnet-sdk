using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class SimpleIntentSchedulerTests
{
    private IFeeEstimator _feeEstimator;
    private IClientTransport _transport;
    private IContractService _contractService;
    private IBitcoinBlockchain _blockchain;
    private SimpleIntentScheduler _scheduler;

    [SetUp]
    public void SetUp()
    {
        _feeEstimator = Substitute.For<IFeeEstimator>();
        _feeEstimator.EstimateFeeAsync(Arg.Any<ArkIntentSpec>(), Arg.Any<CancellationToken>())
            .Returns(0L);

        _transport = Substitute.For<IClientTransport>();
        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(CreateServerInfo());

        _blockchain = Substitute.For<IBitcoinBlockchain>();
        _blockchain.GetChainTime(Arg.Any<CancellationToken>())
            .Returns(new TimeHeight(DateTimeOffset.UtcNow, 100));

        var outputContract = CreateContractSubstitute();
        var serverKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        var tweakedKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        outputContract.GetArkAddress(Arg.Any<OutputDescriptor?>())
            .Returns(new ArkAddress(tweakedKey, serverKey));

        _contractService = Substitute.For<IContractService>();
        _contractService.DeriveContract(
                Arg.Any<string>(), Arg.Any<NextContractPurpose>(), Arg.Any<ArkContract[]>(),
                Arg.Any<ContractActivityState>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(outputContract);

        _scheduler = new SimpleIntentScheduler(
            _feeEstimator, _transport, _contractService, _blockchain,
            Options.Create(new SimpleIntentSchedulerOptions { Threshold = TimeSpan.FromHours(1) }));
    }

    [Test]
    public async Task Chunks_LargeWallet_IntoMultipleIntents()
    {
        // 120 eligible VTXOs must split into 50 + 50 + 20, not one oversized intent.
        var coins = Enumerable.Range(0, 120).Select(_ => CreateCoin(1000)).ToList();

        var intents = await _scheduler.GetIntentsToSubmit(coins);

        Assert.That(intents, Has.Count.EqualTo(3));
        var sizes = intents.Select(i => i.Coins.Length).OrderByDescending(x => x).ToArray();
        Assert.That(sizes, Is.EqualTo(new[] { 50, 50, 20 }));
        Assert.That(intents, Has.All.Matches<ArkIntentSpec>(i =>
            i.Coins.Length <= ArkTransactionLimits.MaxVtxosPerArkTransaction));

        // Every coin lands in exactly one intent.
        var allOutpoints = intents.SelectMany(i => i.Coins).Select(c => c.Outpoint).ToList();
        Assert.That(allOutpoints, Is.Unique);
        Assert.That(allOutpoints, Has.Count.EqualTo(120));
    }

    [Test]
    public async Task Skips_SubDustTailChunk_ButKeepsFullChunks()
    {
        // Coins sort descending before chunking, so the lone 100-sat coin lands
        // in the tail chunk, whose sum is below dust (546) → only the full chunk
        // becomes an intent.
        var coins = Enumerable.Range(0, 50).Select(_ => CreateCoin(1000)).ToList();
        coins.Add(CreateCoin(100));

        var intents = await _scheduler.GetIntentsToSubmit(coins);

        Assert.That(intents, Has.Count.EqualTo(1));
        Assert.That(intents.First().Coins, Has.Length.EqualTo(50));
    }

    [Test]
    public async Task Skips_FeeNegativeChunk_Individually()
    {
        // A flat 2000-sat fee per intent: the 2-coin tail chunk (1200 sats, above
        // dust) goes fee-negative and is skipped; the 50-coin chunk still goes through.
        _feeEstimator.EstimateFeeAsync(Arg.Any<ArkIntentSpec>(), Arg.Any<CancellationToken>())
            .Returns(2000L);

        var coins = Enumerable.Range(0, 50).Select(_ => CreateCoin(1000)).ToList();
        coins.Add(CreateCoin(600));
        coins.Add(CreateCoin(600));

        var intents = await _scheduler.GetIntentsToSubmit(coins);

        Assert.That(intents, Has.Count.EqualTo(1));
        Assert.That(intents.First().Coins, Has.Length.EqualTo(50));
    }

    [Test]
    public async Task GroupsByWallet_BeforeChunking()
    {
        // wallet-a: 60 coins → 50 + 10; wallet-b: 10 coins → 10. No intent mixes wallets.
        var coins = Enumerable.Range(0, 60).Select(_ => CreateCoin(1000, "wallet-a")).ToList();
        coins.AddRange(Enumerable.Range(0, 10).Select(_ => CreateCoin(1000, "wallet-b")));

        var intents = await _scheduler.GetIntentsToSubmit(coins);

        Assert.That(intents, Has.Count.EqualTo(3));
        Assert.That(intents, Has.All.Matches<ArkIntentSpec>(i =>
            i.Coins.Select(c => c.WalletIdentifier).Distinct().Count() == 1));
    }

    private static ArkServerInfo CreateServerInfo()
    {
        var serverKey = KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

        var emptyMultisig = new NArk.Core.Scripts.NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());

        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: serverKey,
            DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(),
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
            CheckpointTapScript: new NArk.Core.Scripts.UnilateralPathArkTapScript(
                new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            VtxoMinAmount: Money.Zero,
            VtxoMaxAmount: Money.Coins(21_000_000m),
            UtxoMinAmount: Money.Zero,
            UtxoMaxAmount: Money.Coins(21_000_000m));
    }

    private static ArkContract CreateContractSubstitute()
    {
        return Substitute.For<ArkContract>(
            KeyExtensions.ParseOutputDescriptor(
                "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
                Network.RegTest));
    }

    private static ArkCoin CreateCoin(long satoshis, string walletId = "test-wallet")
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(satoshis), script);

        var scriptBuilder = Substitute.For<NArk.Abstractions.Scripts.ScriptBuilder>();
        scriptBuilder.BuildScript().Returns(Enumerable.Empty<Op>());
        scriptBuilder.Build().Returns(new TapScript(Script.Empty, TapLeafVersion.C0));

        // unrolled + non-null expiry makes the coin eligible via the filter's first
        // clause, isolating these tests from the threshold/recoverability rules.
        return new ArkCoin(
            walletIdentifier: walletId,
            contract: CreateContractSubstitute(),
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
            expiresAtHeight: null,
            outPoint: outpoint,
            txOut: txOut,
            signerDescriptor: null,
            spendingScriptBuilder: scriptBuilder,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: new Sequence(1),
            swept: false,
            unrolled: true,
            assets: null);
    }
}
