using NArk.Abstractions.Settlement;
using NArk.Core.Settlement;
using NSubstitute;

namespace NArk.Tests.Settlement;

[TestFixture]
public class CompositeSettlementServiceTests
{
    private static readonly SettlementDestination Onchain = SettlementDestination.BitcoinOnchain("bcrt1qexample");
    private static readonly SettlementDestination Offchain = SettlementDestination.Ark("ark1qexample");

    private static ISettlementService Rail(
        SettlementDestination handles,
        bool available = true,
        string? unavailableReason = null)
    {
        var rail = Substitute.For<ISettlementService>();
        rail.Available.Returns(available);
        rail.UnavailableReason.Returns(available ? null : unavailableReason);
        rail.CanSettle(Arg.Any<SettlementDestination>())
            .Returns(call => call.Arg<SettlementDestination>().Is(handles.Network, handles.Asset));
        rail.SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                new SettlementResult("transfer", call.Arg<SettlementRequest>().AmountSats,
                    call.Arg<SettlementRequest>().AmountSats, 0)));
        return rail;
    }

    [Test]
    public async Task RoutesToTheRailThatHandlesTheDestination()
    {
        var onchainRail = Rail(Onchain);
        var offchainRail = Rail(Offchain);
        var composite = new CompositeSettlementService([onchainRail, offchainRail]);

        var request = new SettlementRequest("wallet", 50_000, Offchain);
        await composite.SettleAsync(request);

        await offchainRail.Received(1).SettleAsync(request, Arg.Any<CancellationToken>());
        await onchainRail.DidNotReceive().SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PrefersTheFirstRegisteredRail_WhenSeveralHandleTheDestination()
    {
        var first = Rail(Onchain);
        var second = Rail(Onchain);
        var composite = new CompositeSettlementService([first, second]);

        await composite.SettleAsync(new SettlementRequest("wallet", 50_000, Onchain));

        await first.Received(1).SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>());
        await second.DidNotReceive().SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SkipsUnavailableRails()
    {
        var unavailable = Rail(Onchain, available: false, unavailableReason: "provider offline");
        var available = Rail(Onchain);
        var composite = new CompositeSettlementService([unavailable, available]);

        await composite.SettleAsync(new SettlementRequest("wallet", 50_000, Onchain));

        await unavailable.DidNotReceive().SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>());
        await available.Received(1).SettleAsync(Arg.Any<SettlementRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Throws_WhenNoRailHandlesTheDestination()
    {
        var composite = new CompositeSettlementService([Rail(Offchain)]);

        var ex = Assert.ThrowsAsync<SettlementNotSupportedException>(() =>
            composite.SettleAsync(new SettlementRequest("wallet", 50_000, Onchain)));

        Assert.That(ex!.Destination, Is.EqualTo(Onchain));
        Assert.That(ex.Message, Does.Contain("bitcoin/BTC"));
    }

    [Test]
    public void ThrowsWithTheReason_WhenTheOnlyMatchingRailIsUnavailable()
    {
        var composite = new CompositeSettlementService(
            [Rail(Onchain, available: false, unavailableReason: "provider offline")]);

        var ex = Assert.ThrowsAsync<SettlementNotSupportedException>(() =>
            composite.SettleAsync(new SettlementRequest("wallet", 50_000, Onchain)));

        Assert.That(ex!.Message, Does.Contain("provider offline"));
    }

    [Test]
    public void ReportsUnavailable_WhenEveryRailIsUnavailable()
    {
        var composite = new CompositeSettlementService(
            [Rail(Onchain, available: false, unavailableReason: "provider offline")]);

        Assert.Multiple(() =>
        {
            Assert.That(composite.Available, Is.False);
            Assert.That(composite.UnavailableReason, Is.EqualTo("provider offline"));
            Assert.That(composite.CanSettle(Onchain), Is.False);
        });
    }

    [Test]
    public void ReportsUnavailable_WhenNoRailIsRegistered()
    {
        var composite = new CompositeSettlementService([]);

        Assert.Multiple(() =>
        {
            Assert.That(composite.Available, Is.False);
            Assert.That(composite.UnavailableReason, Is.EqualTo("No settlement rail is registered."));
        });
    }
}
