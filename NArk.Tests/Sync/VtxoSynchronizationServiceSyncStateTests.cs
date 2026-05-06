using System.Globalization;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NSubstitute;

namespace NArk.Tests.Sync;

/// <summary>
/// Verifies the wiring between <see cref="VtxoSynchronizationService"/> and
/// <see cref="IWalletStorage"/>: the cold-start catch-up reads
/// <c>MIN(per-wallet vtxo.lastFullPollAt)</c> as its <c>after</c> filter, and
/// successful full-set polls advance the cursor on every wallet.
/// </summary>
[TestFixture]
public class VtxoSynchronizationServiceSyncStateTests
{
    private IVtxoStorage _vtxoStorage = null!;
    private IClientTransport _transport = null!;
    private IActiveScriptsProvider _provider = null!;
    private IWalletStorage _walletStorage = null!;

    [SetUp]
    public void SetUp()
    {
        _vtxoStorage = Substitute.For<IVtxoStorage>();
        _transport = Substitute.For<IClientTransport>();
        _provider = Substitute.For<IActiveScriptsProvider>();
        _walletStorage = Substitute.For<IWalletStorage>();
    }

    [Test]
    public async Task ColdStart_PassesMinPerWalletCursorAsAfterFilter()
    {
        // Wallet A has the older cursor — that's what MIN should yield.
        var older = new DateTimeOffset(2026, 04, 25, 09, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 04, 25, 18, 0, 0, TimeSpan.Zero);
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkWalletInfo>>(new HashSet<ArkWalletInfo>
            {
                MakeWallet("a", older),
                MakeWallet("b", newer),
            }));

        var scripts = new HashSet<string> { "5120aa", "5120bb" };
        _provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scripts));
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => System.Linq.AsyncEnumerable.Empty<ArkVtxo>());

        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [_provider], _walletStorage);

        await sut.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(() => _transport.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientTransport.GetVtxoByScriptsAsSnapshot)));
        await sut.DisposeAsync();

        _transport.Received().GetVtxoByScriptsAsSnapshot(
            Arg.Is<IReadOnlySet<string>>(s => s.SetEquals(scripts)),
            Arg.Is<DateTimeOffset?>(t => t == older),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ColdStart_AnyMissingCursor_FallsBackToNullAfter()
    {
        // Wallet A has a cursor, B is fresh (no metadata) — MIN must conservatively
        // return null so B's first-time scripts don't get skipped.
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkWalletInfo>>(new HashSet<ArkWalletInfo>
            {
                MakeWallet("a", new DateTimeOffset(2026, 04, 25, 09, 0, 0, TimeSpan.Zero)),
                MakeWallet("b", null),
            }));

        var scripts = new HashSet<string> { "5120aa" };
        _provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scripts));
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => System.Linq.AsyncEnumerable.Empty<ArkVtxo>());

        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [_provider], _walletStorage);

        await sut.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(() => _transport.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientTransport.GetVtxoByScriptsAsSnapshot)));
        await sut.DisposeAsync();

        _transport.Received().GetVtxoByScriptsAsSnapshot(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Is<DateTimeOffset?>(t => t == null),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ColdStart_NoWallets_FallsBackToNullAfter()
    {
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkWalletInfo>>(new HashSet<ArkWalletInfo>()));

        var scripts = new HashSet<string> { "5120aa" };
        _provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scripts));
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => System.Linq.AsyncEnumerable.Empty<ArkVtxo>());

        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [_provider], _walletStorage);

        await sut.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(() => _transport.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientTransport.GetVtxoByScriptsAsSnapshot)));
        await sut.DisposeAsync();

        _transport.Received().GetVtxoByScriptsAsSnapshot(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Is<DateTimeOffset?>(t => t == null),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoWalletStorage_StillWorks_WithNullAfter()
    {
        var scripts = new HashSet<string> { "5120aa" };
        _provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scripts));
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => System.Linq.AsyncEnumerable.Empty<ArkVtxo>());

        // walletStorage = null → opt-out; service must still function.
        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [_provider], walletStorage: null);

        await sut.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(() => _transport.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientTransport.GetVtxoByScriptsAsSnapshot)));
        await sut.DisposeAsync();

        _transport.Received().GetVtxoByScriptsAsSnapshot(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Is<DateTimeOffset?>(t => t == null),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ColdStart_OnSuccessfulCatchup_AdvancesCursorOnEveryWallet()
    {
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkWalletInfo>>(new HashSet<ArkWalletInfo>
            {
                MakeWallet("a", null),
                MakeWallet("b", null),
            }));

        var scripts = new HashSet<string> { "5120aa" };
        _provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scripts));
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => System.Linq.AsyncEnumerable.Empty<ArkVtxo>());

        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [_provider], _walletStorage);

        var beforeStart = DateTimeOffset.UtcNow;
        await sut.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(() => _walletStorage.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IWalletStorage.SetMetadataValue)));
        await sut.DisposeAsync();

        // One write per wallet, with the cursor key and a parseable ISO timestamp.
        await _walletStorage.Received().SetMetadataValue(
            "a",
            VtxoSynchronizationService.LastFullPollAtMetadataKey,
            Arg.Is<string?>(v => v != null && IsTimestampInRange(v, beforeStart)),
            Arg.Any<CancellationToken>());
        await _walletStorage.Received().SetMetadataValue(
            "b",
            VtxoSynchronizationService.LastFullPollAtMetadataKey,
            Arg.Is<string?>(v => v != null && IsTimestampInRange(v, beforeStart)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ColdStart_OnCatchupFailure_DoesNotAdvanceCursor()
    {
        // Stored cursor exists; transport throws → cursor must stay put. This
        // is the gap-loss safety net: a failure-then-success sequence cannot
        // collapse the catch-up window onto "now".
        var stored = new DateTimeOffset(2026, 04, 01, 0, 0, 0, TimeSpan.Zero);
        _walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<ArkWalletInfo>>(new HashSet<ArkWalletInfo>
            {
                MakeWallet("a", stored),
            }));

        var scripts = new HashSet<string> { "5120aa" };
        _provider.GetActiveScripts(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scripts));
        _transport.GetVtxoByScriptsAsSnapshot(
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Throwing());

        var sut = new VtxoSynchronizationService(
            _vtxoStorage, _transport, [_provider], _walletStorage);

        await sut.StartAsync(CancellationToken.None);
        await WaitForCallsAsync(() => _transport.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientTransport.GetVtxoByScriptsAsSnapshot)));
        await sut.DisposeAsync();

        await _walletStorage.DidNotReceive().SetMetadataValue(
            Arg.Any<string>(),
            VtxoSynchronizationService.LastFullPollAtMetadataKey,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private static ArkWalletInfo MakeWallet(string id, DateTimeOffset? cursor) =>
        new(
            Id: id,
            Secret: "secret-" + id,
            Destination: null,
            WalletType: WalletType.HD,
            AccountDescriptor: null,
            LastUsedIndex: 0,
            Metadata: cursor is null
                ? null
                : new Dictionary<string, string>
                {
                    [VtxoSynchronizationService.LastFullPollAtMetadataKey] =
                        cursor.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                });

    private static bool IsTimestampInRange(string iso, DateTimeOffset since)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)) return false;
        return parsed >= since && parsed <= DateTimeOffset.UtcNow.AddSeconds(2);
    }

    private static async IAsyncEnumerable<ArkVtxo> Throwing()
    {
        await Task.Yield();
        throw new InvalidOperationException("simulated transport failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async Task WaitForCallsAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
    }
}
