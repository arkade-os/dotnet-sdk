using NArk.Abstractions.VirtualTxs;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests;

[TestFixture]
public class VirtualTxServiceTests
{
    private IClientTransport _transport = null!;
    private IVirtualTxStorage _storage = null!;
    private VirtualTxService _service = null!;
    private readonly OutPoint _testOutpoint = new(uint256.Parse("abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234"), 0);

    [SetUp]
    public void SetUp()
    {
        _transport = Substitute.For<IClientTransport>();
        _storage = Substitute.For<IVirtualTxStorage>();
        _service = new VirtualTxService(_transport, _storage);
    }

    [Test]
    public async Task FetchAndStoreBranch_FullMode_StoresTxidsAndHex()
    {
        // Arrange
        _storage.HasBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(false);

        var chainEntries = new List<VtxoChainEntry>
        {
            new("commitmenttxid", DateTimeOffset.UtcNow.AddDays(30), ChainedTxType.Commitment, []),
            new("treetxid1", DateTimeOffset.UtcNow.AddDays(30), ChainedTxType.Tree, []),
            new("treetxid2", DateTimeOffset.UtcNow.AddDays(30), ChainedTxType.Tree, [])
        };
        _transport.GetVtxoChainAsync(_testOutpoint, Arg.Any<CancellationToken>())
            .Returns(chainEntries);

        var hexList = new List<string> { "deadbeef01", "deadbeef02" };
        _transport.GetVirtualTxsAsync(
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 2),
            Arg.Any<CancellationToken>())
            .Returns(hexList);

        // Act
        await _service.FetchAndStoreBranchAsync(_testOutpoint, VirtualTxMode.Full);

        // Assert
        await _storage.Received(1).UpsertVirtualTxsAsync(
            Arg.Is<IReadOnlyList<VirtualTx>>(txs =>
                txs.Count == 2 &&
                txs[0].Txid == "treetxid1" && txs[0].Hex == "deadbeef01" &&
                txs[1].Txid == "treetxid2" && txs[1].Hex == "deadbeef02"),
            Arg.Any<CancellationToken>());

        await _storage.Received(1).SetBranchAsync(
            _testOutpoint,
            Arg.Is<IReadOnlyList<VtxoBranch>>(b =>
                b.Count == 2 &&
                b[0].Position == 0 && b[0].VirtualTxid == "treetxid1" &&
                b[1].Position == 1 && b[1].VirtualTxid == "treetxid2"),
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
        _transport.GetVtxoChainAsync(_testOutpoint, Arg.Any<CancellationToken>())
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
    public async Task FetchAndStoreBranch_SkipsIfBranchExists()
    {
        // Arrange
        _storage.HasBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        await _service.FetchAndStoreBranchAsync(_testOutpoint);

        // Assert — should not fetch anything
        await _transport.DidNotReceive().GetVtxoChainAsync(
            Arg.Any<OutPoint>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FetchAndStoreBranch_FiltersOutCommitmentType()
    {
        // Arrange
        _storage.HasBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(false);

        var chainEntries = new List<VtxoChainEntry>
        {
            new("commitmenttx", DateTimeOffset.UtcNow, ChainedTxType.Commitment, []),
            new("treetx", DateTimeOffset.UtcNow, ChainedTxType.Tree, [])
        };
        _transport.GetVtxoChainAsync(_testOutpoint, Arg.Any<CancellationToken>())
            .Returns(chainEntries);
        _transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "hex1" });

        // Act
        await _service.FetchAndStoreBranchAsync(_testOutpoint, VirtualTxMode.Full);

        // Assert — only tree tx stored, commitment filtered out
        await _storage.Received(1).UpsertVirtualTxsAsync(
            Arg.Is<IReadOnlyList<VirtualTx>>(txs => txs.Count == 1 && txs[0].Txid == "treetx"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureHexPopulated_FetchesMissingHex()
    {
        // Arrange
        var branch = new List<VirtualTx>
        {
            new("tx1", "existinghex", DateTimeOffset.UtcNow),
            new("tx2", null, DateTimeOffset.UtcNow) // Missing hex
        };
        _storage.GetBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(branch);

        _transport.GetVirtualTxsAsync(
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 1 && l[0] == "tx2"),
            Arg.Any<CancellationToken>())
            .Returns(new List<string> { "newhex" });

        // Act
        await _service.EnsureHexPopulatedAsync(_testOutpoint);

        // Assert — only missing tx fetched and updated
        await _storage.Received(1).UpsertVirtualTxsAsync(
            Arg.Is<IReadOnlyList<VirtualTx>>(txs =>
                txs.Count == 1 && txs[0].Txid == "tx2" && txs[0].Hex == "newhex"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureHexPopulated_NoBranchStored_FetchesFresh()
    {
        // Arrange — no branch stored
        _storage.GetBranchAsync(_testOutpoint, Arg.Any<CancellationToken>())
            .Returns(new List<VirtualTx>());
        _storage.HasBranchAsync(_testOutpoint, Arg.Any<CancellationToken>()).Returns(false);

        var chainEntries = new List<VtxoChainEntry>
        {
            new("tx1", DateTimeOffset.UtcNow, ChainedTxType.Tree, [])
        };
        _transport.GetVtxoChainAsync(_testOutpoint, Arg.Any<CancellationToken>())
            .Returns(chainEntries);
        _transport.GetVirtualTxsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "hex1" });

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
}
