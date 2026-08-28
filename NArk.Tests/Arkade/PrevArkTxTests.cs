using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.VirtualTxs;
using NArk.Arkade.Emulator;
using NArk.Core.Transport;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.Arkade;

/// <summary>
/// Covers the <c>prevarktx</c> annotation the emulator requires on every input of a
/// submitted Arkade transaction / intent proof (emulator v0.0.7,
/// <c>internal/application/prevout.go</c>): resolution of the previous transactions
/// (<see cref="PrevArkTxProvider"/>) and their attachment to the PSBT
/// (<see cref="ArkadePsbtExtensions.AttachPrevArkTxsAsync"/> /
/// <see cref="ArkadePsbtExtensions.AttachIntentPrevArkTxsAsync"/>).
/// </summary>
[TestFixture]
public class PrevArkTxTests
{
    private static readonly Network Net = Network.RegTest;

    [Test]
    public async Task AttachPrevArkTxs_AnnotatesEveryInput_WithTheCheckpointsFundingTx()
    {
        // funding → checkpoint → arkTx, twice: the emulator wants the *funding* tx on the
        // Arkade input, not the checkpoint the input actually spends.
        var (fundingA, checkpointA) = Chain(Money.Coins(1));
        var (fundingB, checkpointB) = Chain(Money.Coins(2));
        var arkTx = SpendAll(checkpointA, checkpointB);

        await arkTx.AttachPrevArkTxsAsync(
            [checkpointA, checkpointB], StubProvider(fundingA, fundingB));

        Assert.That(arkTx.Inputs[0].GetArkFieldPrevArkTx(Net)!.GetHash(), Is.EqualTo(fundingA.GetHash()));
        Assert.That(arkTx.Inputs[1].GetArkFieldPrevArkTx(Net)!.GetHash(), Is.EqualTo(fundingB.GetHash()));
    }

    [Test]
    public async Task AttachPrevArkTxs_LeavesSignaturesIntact()
    {
        // The field lives in the PSBT unknown map, which no sighash covers — that is the
        // whole reason attaching it after signing is allowed. Assert the signature survives.
        var (funding, checkpoint) = Chain(Money.Coins(1));
        var arkTx = SpendAll(checkpoint);

        var key = new Key();
        var leafHash = new uint256(3);
        arkTx.Inputs[0].SetTaprootScriptSpendSignature(
            NBitcoin.Secp256k1.ECXOnlyPubKey.Create(key.PubKey.TaprootInternalKey.ToBytes()),
            leafHash, DummySchnorrSig());
        var before = arkTx.Inputs[0].GetTaprootScriptSpendSignatures().Count;

        await arkTx.AttachPrevArkTxsAsync([checkpoint], StubProvider(funding));

        Assert.That(arkTx.Inputs[0].GetTaprootScriptSpendSignatures(), Has.Count.EqualTo(before));
        Assert.That(arkTx.Inputs[0].GetArkFieldPrevArkTx(Net), Is.Not.Null);
    }

    [Test]
    public void AttachPrevArkTxs_ThrowsWhenAPreviousTxCannotBeResolved()
    {
        var (_, checkpoint) = Chain(Money.Coins(1));
        var arkTx = SpendAll(checkpoint);

        // Provider resolves nothing — the emulator would answer "missing prevout tx for
        // input 0"; we must fail before submitting, naming the input and the txid.
        Assert.That(async () => await arkTx.AttachPrevArkTxsAsync([checkpoint], StubProvider()),
            Throws.InstanceOf<InvalidOperationException>()
                .With.Message.Contains("input 0")
                .And.Message.Contains(checkpoint.GetGlobalTransaction().Inputs[0].PrevOut.Hash.ToString()));
    }

    [Test]
    public void AttachPrevArkTxs_ThrowsWhenAnInputHasNoCheckpoint()
    {
        var (fundingA, checkpointA) = Chain(Money.Coins(1));
        var (_, checkpointB) = Chain(Money.Coins(2));
        var arkTx = SpendAll(checkpointA, checkpointB);

        // Only one checkpoint supplied for a two-input spend.
        Assert.That(async () => await arkTx.AttachPrevArkTxsAsync([checkpointA], StubProvider(fundingA)),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("no checkpoint"));
    }

    [Test]
    public void AttachPrevArkTxs_ThrowsWhenACheckpointHasMoreThanOneInput()
    {
        // The emulator requires exactly one input per checkpoint, and the funding tx is
        // read off that input — a multi-input checkpoint has no single answer.
        var (_, checkpointA) = Chain(Money.Coins(1));
        var (_, checkpointB) = Chain(Money.Coins(2));
        var multiInput = SpendAll(checkpointA, checkpointB);
        var arkTx = SpendAll(multiInput);

        Assert.That(async () => await arkTx.AttachPrevArkTxsAsync([multiInput], StubProvider()),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("exactly one"));
    }

    [Test]
    public async Task AttachIntentPrevArkTxs_SkipsInput0_AndAnnotatesTheVtxoInputs()
    {
        // Input 0 of a BIP322 intent proof is the message input; the emulator synthesises
        // its prevout and rejects a proof of fewer than 2 inputs.
        // The message input's parent is deliberately absent from the provider: if the
        // helper asked for it, the call would throw instead of skipping input 0.
        var messageInput = SampleTx(Money.Coins(0));
        var fundingA = SampleTx(Money.Coins(1));
        var fundingB = SampleTx(Money.Coins(2));
        var proof = SpendAll(messageInput, fundingA, fundingB);

        await proof.AttachIntentPrevArkTxsAsync(StubProvider(fundingA, fundingB));

        Assert.That(proof.Inputs[0].GetArkFieldPrevArkTx(Net), Is.Null,
            "the BIP322 message input must not carry a prevarktx field");
        Assert.That(proof.Inputs[1].GetArkFieldPrevArkTx(Net)!.GetHash(), Is.EqualTo(fundingA.GetHash()));
        Assert.That(proof.Inputs[2].GetArkFieldPrevArkTx(Net)!.GetHash(), Is.EqualTo(fundingB.GetHash()));
    }

    [Test]
    public void AttachIntentPrevArkTxs_ThrowsOnSingleInputProof()
    {
        var proof = SpendAll(SampleTx(Money.Coins(1)));

        Assert.That(async () => await proof.AttachIntentPrevArkTxsAsync(StubProvider()),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("at least 2 inputs"));
    }

    [Test]
    public async Task Provider_KeysIndexerResultsByParsedTxid_NotRequestOrder()
    {
        // arkd's GetVirtualTxs returns hexes in DB order, not request order. Pairing them
        // positionally would attach one transaction's body to another's txid.
        var a = SampleTx(Money.Coins(1));
        var b = SampleTx(Money.Coins(2));

        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([Psbt(b), Psbt(a)]); // deliberately reversed

        var resolved = await new PrevArkTxProvider(transport)
            .ResolveAsync([a.GetHash(), b.GetHash()], Net);

        Assert.That(resolved[a.GetHash()].GetHash(), Is.EqualTo(a.GetHash()));
        Assert.That(resolved[b.GetHash()].GetHash(), Is.EqualTo(b.GetHash()));
    }

    [Test]
    public async Task Provider_DropsTransactionsThatWereNotRequested()
    {
        // A server answering with a transaction of its own choosing must not end up
        // attached to an input: the emulator would reject it, but silently carrying
        // unrequested data into a submission is worse than resolving nothing.
        var wanted = SampleTx(Money.Coins(1));
        var unrelated = SampleTx(Money.Coins(7));

        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([Psbt(unrelated)]);

        var resolved = await new PrevArkTxProvider(transport).ResolveAsync([wanted.GetHash()], Net);

        Assert.That(resolved, Is.Empty);
    }

    [Test]
    public async Task Provider_PrefersStorage_AndOnlyFetchesWhatIsMissing()
    {
        var stored = SampleTx(Money.Coins(1));
        var remote = SampleTx(Money.Coins(2));

        var storage = Substitute.For<IVirtualTxStorage>();
        storage.GetVirtualTxAsync(stored.GetHash().ToString(), Arg.Any<CancellationToken>())
            .Returns(new VirtualTx(stored.GetHash().ToString(), Psbt(stored), null, ChainedTxType.Ark));

        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([Psbt(remote)]);

        var resolved = await new PrevArkTxProvider(transport, storage)
            .ResolveAsync([stored.GetHash(), remote.GetHash()], Net);

        Assert.That(resolved, Has.Count.EqualTo(2));
        await transport.Received(1).GetVirtualTxsAsync(
            Arg.Is<IReadOnlyList<string>>(t => t.Count == 1 && t[0] == remote.GetHash().ToString()),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Provider_IgnoresAStoredHexWhoseTxidDoesNotMatch()
    {
        // A mismatched stored body would be rejected by the emulator anyway; fall through
        // to the indexer rather than attaching it.
        var wanted = SampleTx(Money.Coins(1));
        var wrong = SampleTx(Money.Coins(9));

        var storage = Substitute.For<IVirtualTxStorage>();
        storage.GetVirtualTxAsync(wanted.GetHash().ToString(), Arg.Any<CancellationToken>())
            .Returns(new VirtualTx(wanted.GetHash().ToString(), Psbt(wrong), null, ChainedTxType.Ark));

        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([Psbt(wanted)]);

        var resolved = await new PrevArkTxProvider(transport, storage).ResolveAsync([wanted.GetHash()], Net);

        Assert.That(resolved[wanted.GetHash()].GetHash(), Is.EqualTo(wanted.GetHash()));
    }

    [Test]
    public async Task AttachPrevArkTxs_LeavesAnInputThatAlreadyCarriesTheField()
    {
        // The emulator rejects an input bearing two prevarktx fields, so a caller-supplied
        // value (a recursive covenant spending an Arkade tx the indexer cannot serve yet)
        // must win outright rather than be re-resolved.
        var (funding, checkpoint) = Chain(Money.Coins(1));
        var arkTx = SpendAll(checkpoint);
        var caller = SampleTx(Money.Coins(3));
        arkTx.Inputs[0].SetArkFieldPrevArkTx(caller);

        // A provider that would resolve the *other* transaction, and must not be consulted.
        var provider = StubProvider(funding);
        await arkTx.AttachPrevArkTxsAsync([checkpoint], provider);

        Assert.That(arkTx.Inputs[0].GetArkFieldPrevArkTx(Net)!.GetHash(), Is.EqualTo(caller.GetHash()));
        await provider.DidNotReceive().ResolveAsync(
            Arg.Any<IReadOnlyCollection<uint256>>(), Arg.Any<Network>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Provider_FallsBackToChainForParentsTheIndexerCannotServe()
    {
        // A boarding or commitment parent has no off-chain body: getVirtualTxs never
        // returns it, and an intent proof registering a boarding input needs it anyway.
        var boarding = SampleTx(Money.Coins(1));

        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var chain = Substitute.For<IBitcoinBlockchain>();
        chain.GetRawTransactionAsync(boarding.GetHash(), Arg.Any<CancellationToken>()).Returns(boarding);

        var resolved = await new PrevArkTxProvider(transport, virtualTxStorage: null, blockchain: chain)
            .ResolveAsync([boarding.GetHash()], Net);

        Assert.That(resolved[boarding.GetHash()].GetHash(), Is.EqualTo(boarding.GetHash()));
    }

    [Test]
    public async Task Provider_IndexerFailure_StillServesWhatChainHas()
    {
        // An indexer that rejects the whole batch over one unknown txid must not sink a
        // boarding parent the on-chain source could have served.
        var boarding = SampleTx(Money.Coins(1));

        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(_ => throw new HttpRequestException("unknown txid"));

        var chain = Substitute.For<IBitcoinBlockchain>();
        chain.GetRawTransactionAsync(boarding.GetHash(), Arg.Any<CancellationToken>()).Returns(boarding);

        var resolved = await new PrevArkTxProvider(transport, virtualTxStorage: null, blockchain: chain)
            .ResolveAsync([boarding.GetHash()], Net);

        Assert.That(resolved[boarding.GetHash()].GetHash(), Is.EqualTo(boarding.GetHash()));
    }

    [Test]
    public async Task Provider_BackendWithoutRawTxSupport_IsAMissNotACrash()
    {
        // IBitcoinBlockchain.GetRawTransactionAsync defaults to NotSupportedException, so a
        // third-party backend that skips the override must degrade to "unresolved" — the
        // caller then throws naming the txid — rather than taking down the resolve call.
        var wanted = SampleTx(Money.Coins(1));

        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var chain = Substitute.For<IBitcoinBlockchain>();
        chain.GetRawTransactionAsync(Arg.Any<uint256>(), Arg.Any<CancellationToken>())
            .Returns<Task<Transaction?>>(_ => throw new NotSupportedException("no raw tx endpoint"));

        var resolved = await new PrevArkTxProvider(transport, virtualTxStorage: null, blockchain: chain)
            .ResolveAsync([wanted.GetHash()], Net);

        Assert.That(resolved, Is.Empty);
    }

    [Test]
    public async Task Provider_BackendWithoutRawTxSupport_GivesUpAfterTheFirstTxid()
    {
        // NotSupportedException is structural: the backend has no raw-tx endpoint at all, so
        // every remaining txid would fail identically. Asking N times would turn one missing
        // override into N failed calls and N warnings, burying the cause.
        var first = SampleTx(Money.Coins(1));
        var second = SampleTx(Money.Coins(2));

        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var chain = Substitute.For<IBitcoinBlockchain>();
        chain.GetRawTransactionAsync(Arg.Any<uint256>(), Arg.Any<CancellationToken>())
            .Returns<Task<Transaction?>>(_ => throw new NotSupportedException("no raw tx endpoint"));

        var logger = Substitute.For<ILogger<PrevArkTxProvider>>();

        var resolved = await new PrevArkTxProvider(transport, virtualTxStorage: null, blockchain: chain, logger: logger)
            .ResolveAsync([first.GetHash(), second.GetHash()], Net);

        Assert.That(resolved, Is.Empty, "neither txid is resolvable without a raw-tx endpoint");
        await chain.Received(1).GetRawTransactionAsync(Arg.Any<uint256>(), Arg.Any<CancellationToken>());
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<NotSupportedException>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Test]
    public void Provider_IndexerFailure_PropagatesWhenThereIsNoChainToFallBackOn()
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(_ => throw new HttpRequestException("indexer down"));

        Assert.That(async () => await new PrevArkTxProvider(transport)
                .ResolveAsync([SampleTx(Money.Coins(1)).GetHash()], Net),
            Throws.InstanceOf<HttpRequestException>());
    }

    [Test]
    public void AttachedField_SerialisesWithoutAWitnessMarker()
    {
        // The emulator decodes the value with btcd's wire.MsgTx.Deserialize and hashes it
        // against the outpoint. The provider serves a PSBT's unsigned global transaction,
        // so the bytes must be the plain legacy encoding — no segwit marker, no scriptSig.
        var tx = PSBT.FromTransaction(SampleTx(Money.Coins(1)), Net).GetGlobalTransaction();
        var bytes = tx.ToBytes();

        Assert.That(tx.HasWitness, Is.False);
        // version(4) || input count; a segwit encoding would put 0x00 0x01 at offset 4.
        Assert.That(bytes[4], Is.Not.EqualTo(0x00), "unexpected segwit marker in the serialized prevarktx value");
        Assert.That(Transaction.Parse(Convert.ToHexString(bytes), Net).GetHash(), Is.EqualTo(tx.GetHash()));
    }

    [Test]
    public async Task Provider_SkipsUnparseableHexesWithoutAbortingTheBatch()
    {
        var good = SampleTx(Money.Coins(1));
        var alsoGood = SampleTx(Money.Coins(2));

        var transport = Substitute.For<IClientTransport>();
        transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([Psbt(good), "not-a-psbt", Psbt(alsoGood)]);

        var resolved = await new PrevArkTxProvider(transport)
            .ResolveAsync([good.GetHash(), alsoGood.GetHash()], Net);

        Assert.That(resolved, Has.Count.EqualTo(2));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A provider that resolves exactly the given transactions and nothing else.</summary>
    private static IPrevArkTxProvider StubProvider(params Transaction[] txs)
    {
        var byTxid = txs.ToDictionary(t => t.GetHash());
        var provider = Substitute.For<IPrevArkTxProvider>();
        provider.ResolveAsync(Arg.Any<IReadOnlyCollection<uint256>>(), Arg.Any<Network>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyDictionary<uint256, Transaction>>(
                ci.Arg<IReadOnlyCollection<uint256>>()
                    .Where(byTxid.ContainsKey)
                    .Distinct()
                    .ToDictionary(t => t, t => byTxid[t])));
        return provider;
    }

    /// <summary>A funding tx plus the single-input checkpoint that spends its output 0.</summary>
    private static (Transaction Funding, PSBT Checkpoint) Chain(Money amount)
    {
        var funding = SampleTx(amount);
        return (funding, SpendAll(funding));
    }

    /// <summary>A PSBT spending output 0 of each given transaction, one input per parent.</summary>
    private static PSBT SpendAll(params object[] parents)
    {
        var tx = Transaction.Create(Net);
        foreach (var parent in parents)
        {
            var hash = parent switch
            {
                Transaction t => t.GetHash(),
                PSBT p => p.GetGlobalTransaction().GetHash(),
                _ => throw new ArgumentException("parent must be a Transaction or PSBT", nameof(parents)),
            };
            tx.Inputs.Add(new OutPoint(hash, 0));
        }
        tx.Outputs.Add(Money.Coins(0.5m), new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86));
        return PSBT.FromTransaction(tx, Net);
    }

    private static string Psbt(Transaction tx) => PSBT.FromTransaction(tx, Net).ToBase64();

    private static Transaction SampleTx(Money amount)
    {
        var tx = Transaction.Create(Net);
        // A dummy input so the tx serialises; the fresh output key keeps each sample distinct.
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(amount, new Key().GetScriptPubKey(ScriptPubKeyType.TaprootBIP86));
        return tx;
    }

    private static NBitcoin.Secp256k1.SecpSchnorrSignature DummySchnorrSig()
    {
        var bytes = new byte[64];
        bytes[31] = 1; // r = 1
        bytes[63] = 1; // s = 1
        return NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(bytes, out var sig)
            ? sig!
            : throw new InvalidOperationException("could not build a dummy Schnorr signature");
    }
}
