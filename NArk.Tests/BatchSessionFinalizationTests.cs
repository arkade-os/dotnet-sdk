using System.Text.Json;
using NArk.Abstractions;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Batches;
using NArk.Core.Contracts;
using NArk.Core.Helpers;
using NArk.Core.Scripts;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

/// <summary>
/// A forfeit hands a VTXO to the operator, so <see cref="BatchSession"/> must see the
/// intent's outputs in what the operator finalizes before it signs one. These tests drive
/// the finalization event on its own — the phase the operator controls — and check what
/// the session is willing to put its signature on.
/// </summary>
[TestFixture]
public class BatchSessionFinalizationTests
{
    private static readonly Network Net = Network.RegTest;
    private static readonly Script OnchainScript =
        BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Net).ScriptPubKey;

    private IClientTransport _transport = null!;

    [SetUp]
    public void SetUp()
    {
        _transport = Substitute.For<IClientTransport>();
        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(ServerInfo());
    }

    [Test]
    public async Task Finalization_without_a_signed_tree_does_not_forfeit_offchain_intent()
    {
        // Intent pays a VTXO (offchain output), but the operator finalizes without ever
        // publishing or signing a VTXO tree: there is nothing backing the forfeits.
        var session = CreateSession(offchainOutput: true, ins: [Coin()]);
        await session.InitializeAsync();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ProcessEventAsync(new BatchFinalizationEvent(CommitmentTx(), "batch-1")));
        Assert.That(ex!.Message, Does.Contain("Offchain output"));

        await _transport.DidNotReceive().SubmitSignedForfeitTxsAsync(
            Arg.Any<SubmitSignedForfeitTxsRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Finalization_of_an_onchain_only_intent_needs_no_tree()
    {
        // Collaborative exit: everything the intent asks for is in the commitment tx.
        var session = CreateSession(offchainOutput: false, ins: []);
        await session.InitializeAsync();

        Assert.DoesNotThrowAsync(() =>
            session.ProcessEventAsync(new BatchFinalizationEvent(CommitmentTx(OnchainScript), "batch-1")));
    }

    [Test]
    public async Task Finalization_missing_the_onchain_output_is_rejected()
    {
        var session = CreateSession(offchainOutput: false, ins: []);
        await session.InitializeAsync();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ProcessEventAsync(new BatchFinalizationEvent(CommitmentTx(), "batch-1")));
        Assert.That(ex!.Message, Does.Contain("Onchain output"));
    }

    private BatchSession CreateSession(bool offchainOutput, ArkCoin[] ins) =>
        new(
            _transport,
            Substitute.For<IWalletProvider>(),
            new TransactionHelpers.ArkTransactionBuilder(
                _transport, Substitute.For<ISafetyService>(), Substitute.For<IWalletProvider>(),
                Substitute.For<IIntentStorage>()),
            Net,
            Intent(offchainOutput),
            ins,
            new BatchStartedEvent("batch-1", new Sequence(144), []));

    /// <summary>An intent paying a single output, declared either offchain or onchain.</summary>
    private static ArkIntent Intent(bool offchainOutput)
    {
        var proof = Psbt(OnchainScript).ToBase64();
        var message = JsonSerializer.Serialize(new
        {
            type = "register",
            onchain_output_indexes = offchainOutput ? Array.Empty<int>() : [0],
            valid_at = 0,
            expire_at = 0,
            cosigners_public_keys = Array.Empty<string>()
        });

        return new ArkIntent(
            IntentTxId: uint256.One.ToString(),
            IntentId: "intent-1",
            WalletId: "test-wallet",
            State: ArkIntentState.BatchInProgress,
            ValidFrom: null,
            ValidUntil: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            RegisterProof: proof,
            RegisterProofMessage: message,
            DeleteProof: string.Empty,
            DeleteProofMessage: string.Empty,
            BatchId: "batch-1",
            CommitmentTransactionId: null,
            CancellationReason: null,
            IntentVtxos: [],
            SignerDescriptor: NewKey().ToOutputDescriptor(Net).ToString());
    }

    /// <summary>Commitment tx as the operator would publish it, paying the given scripts.</summary>
    private static string CommitmentTx(params Script[] outputs) => Psbt(outputs).ToBase64();

    private static PSBT Psbt(params Script[] outputs)
    {
        var tx = Transaction.Create(Net);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        foreach (var output in outputs)
            tx.Outputs.Add(new TxOut(Money.Satoshis(100_000), output));
        return PSBT.FromTransaction(tx, Net);
    }

    private static ArkCoin Coin()
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var contract = new GenericArkContract(NewKey().ToOutputDescriptor(Net), [script]);
        return new ArkCoin(
            walletIdentifier: "test-wallet",
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.One, 0),
            txOut: new TxOut(Money.Satoshis(100_000), Script.Empty),
            signerDescriptor: NewKey().ToOutputDescriptor(Net),
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false);
    }

    private static ArkServerInfo ServerInfo() =>
        new(
            Dust: Money.Satoshis(546),
            SignerKey: NewKey().ToOutputDescriptor(Net),
            DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance),
            Network: Net,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Net),
            ForfeitPubKey: NewKey(),
            CheckpointTapScript: new UnilateralPathArkTapScript(
                new Sequence(144), new NofNMultisigTapScript([])),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: string.Empty);

    private static ECXOnlyPubKey NewKey()
        => ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
}
