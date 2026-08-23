using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
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
    private const string ServerHex = "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string UserHex   = "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4";

    // With a collaborative-path ArkPaymentContract each input contributes 430 WU.
    // Max inputs before the 40 000 WU limit is breached: 91 (91 × 430 + 214 + 430 = 39 774 WU).
    private const int MaxCoinsPerChunk = 91;

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
        // 120 eligible VTXOs split by proof-tx weight: 91 fill the first chunk (39 774 WU),
        // the remaining 29 form a second chunk.
        var coins = Enumerable.Range(0, 120).Select(_ => CreateCoin(1000)).ToList();

        var intents = await _scheduler.GetIntentsToSubmit(coins);

        Assert.That(intents, Has.Count.EqualTo(2));
        var sizes = intents.Select(i => i.Coins.Length).OrderByDescending(x => x).ToArray();
        Assert.That(sizes, Is.EqualTo(new[] { MaxCoinsPerChunk, 120 - MaxCoinsPerChunk }));

        // Every coin lands in exactly one intent.
        var allOutpoints = intents.SelectMany(i => i.Coins).Select(c => c.Outpoint).ToList();
        Assert.That(allOutpoints, Is.Unique);
        Assert.That(allOutpoints, Has.Count.EqualTo(120));
    }

    [Test]
    public async Task Skips_SubDustTailChunk_ButKeepsFullChunks()
    {
        // 91 coins fill one chunk exactly (39 774 WU); the lone 100-sat coin becomes a
        // second chunk whose sum is below the 546-sat dust threshold and is dropped.
        var coins = Enumerable.Range(0, MaxCoinsPerChunk).Select(_ => CreateCoin(1000)).ToList();
        coins.Add(CreateCoin(100));

        var intents = await _scheduler.GetIntentsToSubmit(coins);

        Assert.That(intents, Has.Count.EqualTo(1));
        Assert.That(intents.First().Coins, Has.Length.EqualTo(MaxCoinsPerChunk));
    }

    [Test]
    public async Task Skips_FeeNegativeChunk_Individually()
    {
        // A flat 2 000-sat fee per intent: the 2-coin tail chunk (1 200 sats total,
        // above dust) goes fee-negative and is skipped; the 91-coin chunk still passes.
        _feeEstimator.EstimateFeeAsync(Arg.Any<ArkIntentSpec>(), Arg.Any<CancellationToken>())
            .Returns(2000L);

        var coins = Enumerable.Range(0, MaxCoinsPerChunk).Select(_ => CreateCoin(1000)).ToList();
        coins.Add(CreateCoin(600));
        coins.Add(CreateCoin(600));

        var intents = await _scheduler.GetIntentsToSubmit(coins);

        Assert.That(intents, Has.Count.EqualTo(1));
        Assert.That(intents.First().Coins, Has.Length.EqualTo(MaxCoinsPerChunk));
    }

    [Test]
    public async Task GroupsByWallet_BeforeChunking()
    {
        // wallet-a: 60 coins fit in one weight-based chunk; wallet-b: 10 coins in one chunk.
        // No intent mixes coins from different wallets.
        var coins = Enumerable.Range(0, 60).Select(_ => CreateCoin(1000, "wallet-a")).ToList();
        coins.AddRange(Enumerable.Range(0, 10).Select(_ => CreateCoin(1000, "wallet-b")));

        var intents = await _scheduler.GetIntentsToSubmit(coins);

        Assert.That(intents, Has.Count.EqualTo(2));
        Assert.That(intents, Has.All.Matches<ArkIntentSpec>(i =>
            i.Coins.Select(c => c.WalletIdentifier).Distinct().Count() == 1));
    }

    [Test]
    public async Task Excludes_pastCutoffDeprecatedCoin_thatNeedsForfeit_butKeepsCurrentSignerCoin()
    {
        // A deprecated signer whose cutoff has already passed: the operator no longer co-signs, so a
        // coin under it that still needs a forfeit cannot join a batch — arkd would reject the whole
        // intent and brick the other coins. It must be held back; the current-signer coin still batches.
        var deprecatedKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(CreateServerInfo(new Dictionary<ECXOnlyPubKey, long>
            {
                { deprecatedKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600 }
            }));

        var nearExpiry = DateTimeOffset.UtcNow.AddMinutes(30); // within the 1h threshold, not yet expired
        var deprecatedCoin = CreateCoinUnder(deprecatedKey.ToOutputDescriptor(Network.RegTest), nearExpiry);
        var currentCoin = CreateCoinUnder(
            KeyExtensions.ParseOutputDescriptor(ServerHex, Network.RegTest),
            nearExpiry);

        var intents = await _scheduler.GetIntentsToSubmit([deprecatedCoin, currentCoin]);

        var selected = intents.SelectMany(i => i.Coins).Select(c => c.Outpoint).ToList();
        Assert.That(selected, Does.Contain(currentCoin.Outpoint), "current-signer coin must still be batched");
        Assert.That(selected, Does.Not.Contain(deprecatedCoin.Outpoint),
            "past-cutoff deprecated coin that needs a forfeit must be held back, not brick the intent");
    }

    [Test]
    public async Task Includes_pastCutoffDeprecatedCoin_onceForfeitFree()
    {
        // Once swept, the coin no longer requires a forfeit (RequiresForfeit() == false), so it can be
        // re-enrolled under the current signer without the old key — it belongs in a batch.
        var deprecatedKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(CreateServerInfo(new Dictionary<ECXOnlyPubKey, long>
            {
                { deprecatedKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600 }
            }));

        var sweptCoin = CreateCoinUnder(
            deprecatedKey.ToOutputDescriptor(Network.RegTest), DateTimeOffset.UtcNow.AddMinutes(30), swept: true);

        var intents = await _scheduler.GetIntentsToSubmit([sweptCoin]);

        var selected = intents.SelectMany(i => i.Coins).Select(c => c.Outpoint).ToList();
        Assert.That(selected, Does.Contain(sweptCoin.Outpoint),
            "a swept (forfeit-free) deprecated coin can re-enroll under the current signer");
    }

    private static ArkServerInfo CreateServerInfo(Dictionary<ECXOnlyPubKey, long>? deprecatedSigners = null)
    {
        var serverKey = KeyExtensions.ParseOutputDescriptor(ServerHex, Network.RegTest);
        var emptyMultisig = new NArk.Core.Scripts.NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());

        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: serverKey,
            DeprecatedSigners: deprecatedSigners ?? new Dictionary<ECXOnlyPubKey, long>(),
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
            CheckpointTapScript: new NArk.Core.Scripts.UnilateralPathArkTapScript(
                new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: "server-digest-abc",
            VtxoMinAmount: Money.Zero,
            VtxoMaxAmount: Money.Coins(21_000_000m),
            UtxoMinAmount: Money.Zero,
            UtxoMaxAmount: Money.Coins(21_000_000m),
            MaxTxWeight: 40_000);
    }

    private static ArkContract CreateContractSubstitute()
    {
        return Substitute.For<ArkContract>(
            KeyExtensions.ParseOutputDescriptor(ServerHex, Network.RegTest));
    }

    private static ArkPaymentContract MakePaymentContract(string serverHex = ServerHex) =>
        new(
            KeyExtensions.ParseOutputDescriptor(serverHex, Network.RegTest),
            new Sequence(144),
            KeyExtensions.ParseOutputDescriptor(UserHex, Network.RegTest));

    private static ArkCoin CreateCoin(long satoshis, string walletId = "test-wallet")
    {
        var contract = MakePaymentContract();
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(satoshis), contract.GetScriptPubKey());

        // unrolled + non-null expiry makes the coin eligible via the filter's first
        // clause, isolating these tests from the threshold/recoverability rules.
        return new ArkCoin(
            walletIdentifier: walletId,
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
            expiresAtHeight: null,
            outPoint: outpoint,
            txOut: txOut,
            signerDescriptor: null,
            spendingScriptBuilder: contract.CollaborativePath(),
            spendingConditionWitness: null,
            lockTime: null,
            sequence: new Sequence(1),
            swept: false,
            unrolled: true,
            assets: null);
    }

    private static ArkCoin CreateCoinUnder(OutputDescriptor serverDescriptor, DateTimeOffset expiresAt,
        bool swept = false, bool unrolled = false, string walletId = "test-wallet")
    {
        var contract = new ArkPaymentContract(serverDescriptor, new Sequence(144),
            KeyExtensions.ParseOutputDescriptor(UserHex, Network.RegTest));
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var txOut = new TxOut(Money.Satoshis(10_000), contract.GetScriptPubKey());

        return new ArkCoin(
            walletIdentifier: walletId,
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: expiresAt,
            expiresAtHeight: null,
            outPoint: outpoint,
            txOut: txOut,
            signerDescriptor: null,
            spendingScriptBuilder: contract.CollaborativePath(),
            spendingConditionWitness: null,
            lockTime: null,
            sequence: new Sequence(1),
            swept: swept,
            unrolled: unrolled,
            assets: null);
    }
}
