using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Scripts;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Fees;
using NArk.Core.Scripts;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

/// <summary>
/// Tests for <see cref="DefaultFeeEstimator"/>, pinned against arkd's
/// <c>arkFeeManager.ComputeIntentFees</c> (internal/infrastructure/feemanager/arkfee.go) and
/// its CEL environments (pkg/ark-lib/arkfee/celenv). Any divergence here is money: arkd
/// rejects an intent that under-pays and silently pockets an over-payment.
/// </summary>
[TestFixture]
public class DefaultFeeEstimatorTests
{
    private const string ServerHex = "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string UserHex = "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4";

    private static readonly Network Net = Network.RegTest;
    private static readonly OutputDescriptor Server = KeyExtensions.ParseOutputDescriptor(ServerHex, Net);
    private static readonly OutputDescriptor User = KeyExtensions.ParseOutputDescriptor(UserHex, Net);

    private static DefaultFeeEstimator CreateEstimator(ArkOperatorFeeTerms feeTerms)
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ServerInfo(feeTerms)));

        var blockchain = Substitute.For<IBitcoinBlockchain>();
        return new DefaultFeeEstimator(transport, blockchain);
    }

    private static ArkServerInfo ServerInfo(ArkOperatorFeeTerms feeTerms) => new(
        Dust: Money.Satoshis(546),
        SignerKey: Server,
        DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance),
        Network: Net,
        UnilateralExit: new Sequence(144),
        BoardingExit: new Sequence(144),
        ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Net),
        ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
        CheckpointTapScript: new UnilateralPathArkTapScript(new Sequence(144), new NofNMultisigTapScript([])),
        FeeTerms: feeTerms,
        Digest: "");

    /// <param name="offchainInput">CEL program arkd evaluates per VTXO input.</param>
    /// <param name="onchainInput">CEL program arkd evaluates per boarding input.</param>
    private static ArkOperatorFeeTerms Terms(
        string offchainInput = "0.0", string onchainInput = "0.0",
        string offchainOutput = "0.0", string onchainOutput = "0.0") =>
        new("1", offchainOutput, onchainOutput, offchainInput, onchainInput);

    private static ArkCoin VtxoCoin(
        long sats = 10_000,
        bool swept = false,
        DateTimeOffset? expiresAt = null,
        uint? expiresAtHeight = null)
    {
        var contract = new ArkPaymentContract(Server, new Sequence(144), User);
        return Coin(contract, contract.CollaborativePath(), sats, swept, expiresAt, expiresAtHeight);
    }

    private static ArkCoin BoardingCoin(long sats = 10_000)
    {
        var contract = new ArkBoardingContract(Server, new Sequence(144), User);
        return Coin(contract, contract.CollaborativePath(), sats, swept: false,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1), expiresAtHeight: null);
    }

    private static ArkCoin Coin(
        NArk.Abstractions.Contracts.ArkContract contract, ScriptBuilder spendingScript, long sats, bool swept,
        DateTimeOffset? expiresAt, uint? expiresAtHeight)
    {
        var needsSequence = spendingScript.BuildScript().Any(op => op.Code == OpcodeType.OP_CHECKSEQUENCEVERIFY);
        return new ArkCoin(
            "wallet", contract,
            birth: DateTimeOffset.UtcNow.AddMinutes(-10),
            expiresAt: expiresAt,
            expiresAtHeight: expiresAtHeight,
            outPoint: new OutPoint(RandomUtils.GetUInt256(), 0),
            txOut: new TxOut(Money.Satoshis(sats), contract.GetScriptPubKey()),
            signerDescriptor: null,
            spendingScriptBuilder: spendingScript,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: needsSequence ? new Sequence(144) : null,
            swept: swept, unrolled: false);
    }

    private static ArkTxOut Out(ArkTxOutType type, long sats) =>
        new(type, Money.Satoshis(sats), new Key().GetAddress(ScriptPubKeyType.TaprootBIP86, Net));

    private static ArkIntentSpec Spec(ArkCoin[] coins, ArkTxOut[] outputs) =>
        new(coins, outputs, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    // ── Variable bindings ─────────────────────────────────────────────────────

    [Test]
    public async Task OffchainInput_BindsInputTypeVariable()
    {
        // arkd's celenv exposes the input type as `inputType`, not `type`. Binding the wrong
        // name makes any type-aware operator program fail to evaluate.
        var estimator = CreateEstimator(Terms(offchainInput: "inputType == 'vtxo' ? 200.0 : 0.0"));

        var fee = await estimator.EstimateFeeAsync(Spec([VtxoCoin()], []));

        Assert.That(fee, Is.EqualTo(Money.Satoshis(200)));
    }

    [Test]
    public async Task OffchainInput_NoteContract_IsTypedAsNote()
    {
        var estimator = CreateEstimator(Terms(offchainInput: "inputType == 'note' ? 50.0 : 999.0"));
        var contract = new ArkPaymentContract(Server, new Sequence(144), User);
        var coin = Coin(contract, contract.CollaborativePath(), 10_000, swept: false,
            DateTimeOffset.UtcNow.AddHours(1), null);

        // A plain VTXO must not be priced as a note.
        var fee = await estimator.EstimateFeeAsync(Spec([coin], []));

        Assert.That(fee, Is.EqualTo(Money.Satoshis(999)));
    }

    [Test]
    public async Task OffchainInput_SweptCoin_IsRecoverable()
    {
        var estimator = CreateEstimator(Terms(offchainInput: "inputType == 'recoverable' ? 0.0 : 500.0"));

        var fee = await estimator.EstimateFeeAsync(Spec([VtxoCoin(swept: true)], []));

        Assert.That(fee, Is.EqualTo(Money.Zero));
    }

    [Test]
    public async Task OffchainInput_ExpiredButUnsweptCoin_IsStillAPlainVtxo()
    {
        // arkd classifies on Swept alone (toArkFeeOffchainInput); treating a merely-expired
        // coin as 'recoverable' would under-estimate wherever recoverable inputs are cheaper.
        var estimator = CreateEstimator(Terms(offchainInput: "inputType == 'recoverable' ? 0.0 : 500.0"));
        var expired = VtxoCoin(swept: false, expiresAt: DateTimeOffset.UtcNow.AddHours(-1));

        var fee = await estimator.EstimateFeeAsync(Spec([expired], []));

        Assert.That(fee, Is.EqualTo(Money.Satoshis(500)));
    }

    [Test]
    public async Task OffchainInput_HeightExpiry_BindsUnixSecondsNotBlockHeight()
    {
        // arkd feeds time.Unix(vtxo.ExpiresAt, 0), always unix seconds. Passing a block height
        // would read as a 1970 timestamp and send an `expiry - now()` program down its
        // "expires imminently" branch — typically the free one.
        var estimator = CreateEstimator(Terms(offchainInput: "expiry - now() < 3600.0 ? 0.0 : 700.0"));
        var heightExpiring = VtxoCoin(expiresAt: null, expiresAtHeight: 850_000);

        var fee = await estimator.EstimateFeeAsync(Spec([heightExpiring], []));

        Assert.That(fee, Is.EqualTo(Money.Zero),
            "a height-only expiry has no timestamp to offer, so it must not masquerade as a far-future one");
    }

    [Test]
    public async Task OffchainInput_TimeExpiry_IsPassedAsUnixSeconds()
    {
        var estimator = CreateEstimator(Terms(offchainInput: "expiry - now() < 3600.0 ? 0.0 : 700.0"));
        var farFuture = VtxoCoin(expiresAt: DateTimeOffset.UtcNow.AddDays(30));

        var fee = await estimator.EstimateFeeAsync(Spec([farFuture], []));

        Assert.That(fee, Is.EqualTo(Money.Satoshis(700)));
    }

    // ── Onchain (boarding) inputs ─────────────────────────────────────────────

    [Test]
    public async Task BoardingInput_UsesTheOnchainInputProgram()
    {
        var estimator = CreateEstimator(Terms(offchainInput: "1000.0", onchainInput: "amount * 0.001"));

        var fee = await estimator.EstimateFeeAsync(Spec([BoardingCoin(50_000)], []));

        Assert.That(fee, Is.EqualTo(Money.Satoshis(50)),
            "a boarding UTXO is an onchain input in arkd and must not be priced by the offchain program");
    }

    [Test]
    public async Task MixedInputs_ArePricedByTheirOwnPrograms()
    {
        var estimator = CreateEstimator(Terms(offchainInput: "30.0", onchainInput: "7.0"));

        var fee = await estimator.EstimateFeeAsync(
            Spec([VtxoCoin(), VtxoCoin(), BoardingCoin()], []));

        Assert.That(fee, Is.EqualTo(Money.Satoshis(67)));
    }

    // ── Outputs ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Outputs_AreSplitBetweenOffchainAndOnchainPrograms()
    {
        var estimator = CreateEstimator(Terms(offchainOutput: "11.0", onchainOutput: "400.0"));

        var fee = await estimator.EstimateFeeAsync(Spec([],
            [Out(ArkTxOutType.Vtxo, 1_000), Out(ArkTxOutType.Onchain, 20_000)]));

        Assert.That(fee, Is.EqualTo(Money.Satoshis(411)));
    }

    // ── Rounding ──────────────────────────────────────────────────────────────

    [Test]
    public async Task FractionalTerms_AreSummedThenRoundedUpOnce()
    {
        // arkd accumulates float64 terms and calls FeeAmount.ToSatoshis() — math.Ceil — on the
        // total. Rounding each term instead would charge 3 sat here where arkd charges 2.
        var estimator = CreateEstimator(Terms(offchainInput: "0.5"));

        var fee = await estimator.EstimateFeeAsync(Spec([VtxoCoin(), VtxoCoin(), VtxoCoin()], []));

        Assert.That(fee, Is.EqualTo(Money.Satoshis(2)));
    }

    [Test]
    public async Task ZeroPrograms_CostNothing()
    {
        var estimator = CreateEstimator(Terms());

        var fee = await estimator.EstimateFeeAsync(
            Spec([VtxoCoin(), BoardingCoin()], [Out(ArkTxOutType.Vtxo, 1_000)]));

        Assert.That(fee, Is.EqualTo(Money.Zero));
    }
}
