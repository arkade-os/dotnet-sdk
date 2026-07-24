using NArk.Abstractions.Extensions;
using NArk.Abstractions.VirtualTxs;
using NArk.Core;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class VirtualTxServiceTests
{
    private IClientTransport _transport = null!;
    private IVirtualTxStorage _storage = null!;
    private IVtxoChainProofProvider _proofProvider = null!;
    private VirtualTxService _service = null!;
    private readonly OutPoint _testOutpoint = new(uint256.Parse("abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234"), 0);

    [SetUp]
    public void SetUp()
    {
        _transport = Substitute.For<IClientTransport>();
        _storage = Substitute.For<IVirtualTxStorage>();
        _proofProvider = Substitute.For<IVtxoChainProofProvider>();
        // Default: no proof available → service falls back to anonymous lookup (null proof/message).
        _proofProvider.TryCreateProofAsync(Arg.Any<OutPoint>(), Arg.Any<CancellationToken>())
            .Returns((ValueTuple<string, string>?)null);
        // The service resolves a network (to parse each returned hex back to its txid).
        _transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(CreateServerInfo());
        _service = new VirtualTxService(_transport, _storage, _proofProvider);
    }

    [Test]
    public async Task FetchAndStoreBranch_FullMode_PairsHexByParsedTxid_RegardlessOfResponseOrder()
    {
        // Arrange
        _storage.HasBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(false);

        // Real virtual txs so the service can parse each hex back to its own txid — the correct,
        // order-proof pairing key. arkd's GetVirtualTxs (SQL `WHERE id IN (...)`) does NOT preserve
        // request order, so the fix must not rely on positional pairing.
        var tree1 = MakeVirtualTx(0x11);
        var tree2 = MakeVirtualTx(0x22);

        var chainEntries = new List<VtxoChainEntry>
        {
            new("commitmenttxid", DateTimeOffset.UtcNow.AddDays(30), ChainedTxType.Commitment, []),
            new(tree1.Txid, DateTimeOffset.UtcNow.AddDays(30), ChainedTxType.Tree, []),
            new(tree2.Txid, DateTimeOffset.UtcNow.AddDays(30), ChainedTxType.Tree, [])
        };
        _transport.GetVtxoChainAsync(_testOutpoint, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(chainEntries);

        // Return the hexes in REVERSED order relative to the request to prove the pairing is by
        // parsed txid, not by position (the old Zip-by-index bug would attach tree2's hex to tree1).
        _transport.GetVirtualTxsAsync(
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 2 && l[0] == tree1.Txid && l[1] == tree2.Txid),
            Arg.Any<CancellationToken>())
            .Returns(new List<string> { tree2.Hex, tree1.Hex });

        // Act
        await _service.FetchAndStoreBranchAsync(_testOutpoint, VirtualTxMode.Full);

        // Assert — each tree tx got ITS OWN hex despite the reversed response order.
        await _storage.Received(1).UpsertVirtualTxsAsync(
            Arg.Is<IReadOnlyList<VirtualTx>>(txs =>
                txs.Count == 3 &&
                txs[0].Txid == "commitmenttxid" && txs[0].Hex == null && txs[0].Type == ChainedTxType.Commitment &&
                txs[1].Txid == tree1.Txid && txs[1].Hex == tree1.Hex && txs[1].Type == ChainedTxType.Tree &&
                txs[2].Txid == tree2.Txid && txs[2].Hex == tree2.Hex && txs[2].Type == ChainedTxType.Tree),
            Arg.Any<CancellationToken>());

        // Branch covers the whole chain so consumers can walk back to the anchor.
        await _storage.Received(1).SetBranchAsync(
            _testOutpoint,
            Arg.Is<IReadOnlyList<VtxoBranch>>(b =>
                b.Count == 3 &&
                b[0].Position == 0 && b[0].VirtualTxid == "commitmenttxid" &&
                b[1].Position == 1 && b[1].VirtualTxid == tree1.Txid &&
                b[2].Position == 2 && b[2].VirtualTxid == tree2.Txid),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FetchAndStoreBranch_LiteMode_StoresOnlyTxids()
    {
        // Arrange
        _storage.HasBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(false);

        var chainEntries = new List<VtxoChainEntry>
        {
            new("treetxid1", DateTimeOffset.UtcNow.AddDays(30), ChainedTxType.Tree, [])
        };
        _transport.GetVtxoChainAsync(_testOutpoint, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(chainEntries);

        // Act
        await _service.FetchAndStoreBranchAsync(_testOutpoint, VirtualTxMode.Lite);

        // Assert — should NOT call GetVirtualTxsAsync in Lite mode
        await _transport.DidNotReceive().GetVirtualTxsAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());

        await _storage.Received(1).UpsertVirtualTxsAsync(
            Arg.Is<IReadOnlyList<VirtualTx>>(txs =>
                txs.Count == 1 && txs[0].Hex == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FetchAndStoreBranch_LiteMode_SkipsIfBranchExists()
    {
        // Arrange — Lite mode stores txids only, so an existing branch is
        // enough to skip (there's no hex to keep signed).
        _storage.HasBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        await _service.FetchAndStoreBranchAsync(_testOutpoint, VirtualTxMode.Lite);

        // Assert — should not fetch anything
        await _transport.DidNotReceive().GetVtxoChainAsync(
            Arg.Any<OutPoint>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FetchAndStoreBranch_FullMode_RefetchesWhenStoredBranchNotBroadcastReady()
    {
        // Arrange — a branch exists, but its stored hex is a sig-less template
        // ("deadbeef" won't parse to a finalizable witness). Full mode must NOT
        // treat that as done: it self-heals by re-fetching until the signed copy
        // lands, so unilateral exit can broadcast from storage without the operator.
        var tree = MakeVirtualTx(0x33);
        _storage.HasBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(true);
        _storage.GetBranchAsync(_testOutpoint, Arg.Any<CancellationToken>())
            .Returns(new List<VirtualTx> { new(tree.Txid, "deadbeef", null, ChainedTxType.Tree) });

        _transport.GetVtxoChainAsync(_testOutpoint, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VtxoChainEntry>
            {
                new(tree.Txid, DateTimeOffset.UtcNow.AddDays(30), ChainedTxType.Tree, [])
            });
        _transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { tree.Hex });

        // Act
        await _service.FetchAndStoreBranchAsync(_testOutpoint, VirtualTxMode.Full);

        // Assert — did NOT short-circuit; re-fetched the chain to self-heal and stored the fresh hex.
        await _transport.Received().GetVtxoChainAsync(_testOutpoint, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _storage.Received(1).UpsertVirtualTxsAsync(
            Arg.Is<IReadOnlyList<VirtualTx>>(txs => txs.Count == 1 && txs[0].Txid == tree.Txid && txs[0].Hex == tree.Hex),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FetchAndStoreBranch_FetchesHexOnlyForNonCommitmentEntries()
    {
        // Arrange
        _storage.HasBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(false);

        var tree = MakeVirtualTx(0x44);
        var chainEntries = new List<VtxoChainEntry>
        {
            new("commitmenttx", DateTimeOffset.UtcNow, ChainedTxType.Commitment, []),
            new(tree.Txid, DateTimeOffset.UtcNow, ChainedTxType.Tree, [])
        };
        _transport.GetVtxoChainAsync(_testOutpoint, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(chainEntries);
        _transport.GetVirtualTxsAsync(
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 1 && l[0] == tree.Txid),
            Arg.Any<CancellationToken>())
            .Returns(new List<string> { tree.Hex });

        // Act
        await _service.FetchAndStoreBranchAsync(_testOutpoint, VirtualTxMode.Full);

        // Assert — both entries stored (commitment with null hex, tree with
        // its fetched hex). arkd's GetVirtualTxs is the wrong endpoint for
        // commitment txs (those live on-chain), so we never request hex for them.
        await _storage.Received(1).UpsertVirtualTxsAsync(
            Arg.Is<IReadOnlyList<VirtualTx>>(txs =>
                txs.Count == 2 &&
                txs[0].Txid == "commitmenttx" && txs[0].Hex == null &&
                    txs[0].Type == ChainedTxType.Commitment &&
                txs[1].Txid == tree.Txid && txs[1].Hex == tree.Hex &&
                    txs[1].Type == ChainedTxType.Tree),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureHexPopulated_PairsMissingHexByParsedTxid_RegardlessOfResponseOrder()
    {
        // Arrange — two txs missing hex; the indexer returns them in reversed order.
        var tx1 = MakeVirtualTx(0x55);
        var tx2 = MakeVirtualTx(0x66);
        var branch = new List<VirtualTx>
        {
            new("has-hex", "existinghex", DateTimeOffset.UtcNow),
            new(tx1.Txid, null, DateTimeOffset.UtcNow),
            new(tx2.Txid, null, DateTimeOffset.UtcNow)
        };
        _storage.GetBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(branch);

        _transport.GetVirtualTxsAsync(
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 2 && l[0] == tx1.Txid && l[1] == tx2.Txid),
            Arg.Any<CancellationToken>())
            .Returns(new List<string> { tx2.Hex, tx1.Hex }); // reversed

        // Act
        await _service.EnsureHexPopulatedAsync(_testOutpoint);

        // Assert — each missing tx got ITS OWN hex (paired by parsed txid, not position).
        await _storage.Received(1).UpsertVirtualTxsAsync(
            Arg.Is<IReadOnlyList<VirtualTx>>(txs =>
                txs.Count == 2 &&
                txs.Any(t => t.Txid == tx1.Txid && t.Hex == tx1.Hex) &&
                txs.Any(t => t.Txid == tx2.Txid && t.Hex == tx2.Hex)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureHexPopulated_NoBranchStored_FetchesFresh()
    {
        // Arrange — no branch stored
        _storage.GetBranchAsync(_testOutpoint, Arg.Any<CancellationToken>())
            .Returns(new List<VirtualTx>());
        _storage.HasBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(false);

        var tree = MakeVirtualTx(0x77);
        var chainEntries = new List<VtxoChainEntry>
        {
            new(tree.Txid, DateTimeOffset.UtcNow, ChainedTxType.Tree, [])
        };
        _transport.GetVtxoChainAsync(_testOutpoint, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(chainEntries);
        _transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { tree.Hex });

        // Act
        await _service.EnsureHexPopulatedAsync(_testOutpoint);

        // Assert — fetched and stored fresh
        await _storage.Received(1).UpsertVirtualTxsAsync(
            Arg.Any<IReadOnlyList<VirtualTx>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PruneForSpentVtxos_CallsStorageForEach()
    {
        // Arrange
        var outpoints = new List<OutPoint>
        {
            new(uint256.Parse("1111111111111111111111111111111111111111111111111111111111111111"), 0),
            new(uint256.Parse("2222222222222222222222222222222222222222222222222222222222222222"), 1)
        };

        // Act
        await _service.PruneForSpentVtxosAsync(outpoints);

        // Assert
        await _storage.Received(1).PruneForSpentVtxoAsync(outpoints[0], Arg.Any<CancellationToken>());
        await _storage.Received(1).PruneForSpentVtxoAsync(outpoints[1], Arg.Any<CancellationToken>());
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a real, self-consistent virtual tx: a minimal PSBT whose global-tx txid the service can
    /// recover by parsing the hex. The random taproot output keeps every call's txid unique.
    /// </summary>
    private static (string Txid, string Hex) MakeVirtualTx(byte seed)
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat(seed, 32).ToArray()), 0));
        tx.Outputs.Add(Money.Coins(1), new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86));
        var psbt = PSBT.FromTransaction(tx, Network.RegTest);
        return (tx.GetHash().ToString(), psbt.ToBase64());
    }

    private static ArkServerInfo CreateServerInfo()
    {
        var signerKey = OutputDescriptor.Parse(
            $"rawtr({Convert.ToHexString(new Key().PubKey.TaprootInternalKey.ToBytes()).ToLowerInvariant()})",
            Network.RegTest);
        var emptyMultisig = new NArk.Core.Scripts.NofNMultisigTapScript(Array.Empty<NBitcoin.Secp256k1.ECXOnlyPubKey>());

        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: signerKey,
            DeprecatedSigners: new Dictionary<NBitcoin.Secp256k1.ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance),
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: NBitcoin.Secp256k1.ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
            CheckpointTapScript: new NArk.Core.Scripts.UnilateralPathArkTapScript(new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: "");
    }
}
