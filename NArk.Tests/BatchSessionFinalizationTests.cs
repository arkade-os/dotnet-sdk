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
using NBitcoin.Scripting;
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
    private IWalletProvider _walletProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _transport = Substitute.For<IClientTransport>();
        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(ServerInfo());

        // Signer with no cosigner entry in the tree: enough to run the signing phase end to
        // end without producing signatures, which is all these tests need.
        var signer = Substitute.For<IArkadeWalletSigner>();
        signer.GetPubKey(Arg.Any<OutputDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(ECPubKey.Create(new Key().PubKey.ToBytes()));
        _walletProvider = Substitute.For<IWalletProvider>();
        _walletProvider.GetSignerAsync("test-wallet", Arg.Any<CancellationToken>()).Returns(signer);
    }

    [Test]
    public async Task Finalization_without_a_signed_tree_does_not_forfeit_offchain_intent()
    {
        // Intent pays a VTXO (offchain output) and the operator runs the whole sequence, but
        // only ever publishes the connectors tree: there is nothing backing the forfeits.
        var session = CreateSession(offchainOutput: true, ins: [Coin()]);
        await session.InitializeAsync();

        await session.ProcessEventAsync(new TreeSigningStartedEvent(CommitmentTx(), "batch-1", []));
        await session.ProcessEventAsync(new TreeNoncesAggregatedEvent("batch-1", new Dictionary<string, string>()));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ProcessEventAsync(new BatchFinalizationEvent(CommitmentTx(), "batch-1")));
        Assert.That(ex!.Message, Does.Contain("Offchain output"));

        await _transport.DidNotReceive().SubmitSignedForfeitTxsAsync(
            Arg.Any<SubmitSignedForfeitTxsRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Finalization_replacing_the_signed_commitment_tx_is_rejected()
    {
        // The tree is validated and co-signed against one commitment tx, then the operator
        // finalizes a different one — the tree we signed says nothing about that transaction.
        var session = CreateSession(offchainOutput: true, ins: [Coin()]);
        await session.InitializeAsync();

        var signedCommitmentTx = Psbt(OnchainScript);
        await session.ProcessEventAsync(VtxoTreeNode(signedCommitmentTx));
        await session.ProcessEventAsync(
            new TreeSigningStartedEvent(signedCommitmentTx.ToBase64(), "batch-1", []));
        await session.ProcessEventAsync(new TreeNoncesAggregatedEvent("batch-1", new Dictionary<string, string>()));

        var replacement = Psbt(OnchainScript, OnchainScript);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ProcessEventAsync(new BatchFinalizationEvent(replacement.ToBase64(), "batch-1")));
        Assert.That(ex!.Message, Does.Contain("differs from the one signed"));

        await _transport.DidNotReceive().SubmitSignedForfeitTxsAsync(
            Arg.Any<SubmitSignedForfeitTxsRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Finalization_before_the_signing_phases_is_ignored()
    {
        var session = CreateSession(offchainOutput: true, ins: [Coin()]);
        await session.InitializeAsync();

        var isComplete = await session.ProcessEventAsync(new BatchFinalizationEvent(CommitmentTx(), "batch-1"));

        Assert.That(isComplete, Is.False);
        await _transport.DidNotReceive().SubmitSignedForfeitTxsAsync(
            Arg.Any<SubmitSignedForfeitTxsRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Finalization_of_an_onchain_only_intent_needs_no_tree()
    {
        // Collaborative exit: everything the intent asks for is in the commitment tx, and the
        // session starts past the signing phases because it has no tree branch to co-sign.
        var session = CreateSession(offchainOutput: false, ins: []);
        await session.InitializeAsync();

        Assert.DoesNotThrowAsync(() =>
            session.ProcessEventAsync(new BatchFinalizationEvent(CommitmentTx(OnchainScript), "batch-1")));

        // Nothing to forfeit, and the session stays open until the batch is finalized onchain.
        await _transport.DidNotReceive().SubmitSignedForfeitTxsAsync(
            Arg.Any<SubmitSignedForfeitTxsRequest>(), Arg.Any<CancellationToken>());
        Assert.That(session.IsComplete, Is.False);
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

    [Test]
    public async Task Finalization_of_an_unreadable_intent_is_rejected()
    {
        // The registration proof cannot be parsed, so the session has no idea what the intent
        // asked for — and an unknown answer is not the same as "nothing was asked for".
        var session = CreateSession(offchainOutput: false, ins: [], registerProof: "not-a-psbt");
        await session.InitializeAsync();

        await session.ProcessEventAsync(new TreeSigningStartedEvent(CommitmentTx(), "batch-1", []));
        await session.ProcessEventAsync(new TreeNoncesAggregatedEvent("batch-1", new Dictionary<string, string>()));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ProcessEventAsync(new BatchFinalizationEvent(CommitmentTx(OnchainScript), "batch-1")));
        Assert.That(ex!.Message, Does.Contain("no readable outputs"));

        await _transport.DidNotReceive().SubmitSignedForfeitTxsAsync(
            Arg.Any<SubmitSignedForfeitTxsRequest>(), Arg.Any<CancellationToken>());
    }

    private BatchSession CreateSession(bool offchainOutput, ArkCoin[] ins, string? registerProof = null) =>
        new(
            _transport,
            _walletProvider,
            new TransactionHelpers.ArkTransactionBuilder(
                _transport, Substitute.For<ISafetyService>(), _walletProvider,
                Substitute.For<IIntentStorage>()),
            Net,
            Intent(offchainOutput, registerProof),
            ins,
            new BatchStartedEvent("batch-1", new Sequence(144), []));

    /// <summary>An intent paying a single output, declared either offchain or onchain.</summary>
    private static ArkIntent Intent(bool offchainOutput, string? registerProof = null)
    {
        var proof = registerProof ?? Psbt(OnchainScript).ToBase64();
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

    /// <summary>
    /// A one-node VTXO tree spending the commitment tx's shared output and paying the
    /// intent's offchain output, so it passes tree and intent-output validation.
    /// </summary>
    private static TreeTxEvent VtxoTreeNode(PSBT commitmentTx)
    {
        var tx = Transaction.Create(Net);
        tx.Inputs.Add(new TxIn(new OutPoint(commitmentTx.GetGlobalTransaction().GetHash(), 0)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(100_000), OnchainScript));

        return new TreeTxEvent(
            Id: "batch-1",
            BatchIndex: 0,
            Children: new Dictionary<uint, string>(),
            Topic: [],
            Tx: PSBT.FromTransaction(tx, Net).ToBase64(),
            TxId: tx.GetHash().ToString());
    }

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
