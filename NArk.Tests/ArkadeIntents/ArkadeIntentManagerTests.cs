using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents;

/// <summary>
/// Unit coverage for <see cref="ArkadeIntentManager"/>'s <see cref="ArkadeIntentManager.CancelSwap"/>
/// preconditions — the guard rails that run before any transport/emulator/spend work, so they can be
/// asserted without a live stack. The happy-path create/fulfill/cancel flow is exercised end-to-end by
/// <c>NArk.Tests.End2End/Arkade/ArkadeSwapTests</c>.
/// </summary>
[TestFixture]
public class ArkadeIntentManagerTests
{
    private IArkadeIntentStorage _intents = null!;
    private ISpendingService _spending = null!;
    private ArkadeIntentManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _intents = Substitute.For<IArkadeIntentStorage>();
        _spending = Substitute.For<ISpendingService>();
        _manager = new ArkadeIntentManager(
            Substitute.For<IClientTransport>(),
            Substitute.For<IEmulatorProvider>(),
            Substitute.For<IContractService>(),
            Substitute.For<IWalletProvider>(),
            _spending,
            _intents,
            Substitute.For<IVtxoStorage>());
    }

    private void HaveIntents(params ArkadeSwapIntent[] intents) =>
        _intents.GetArkadeSwapIntents(
                Arg.Any<ArkadeSwapIntentStatus?>(), Arg.Any<string>(), Arg.Any<string[]>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>(intents));

    private static ArkadeSwapIntent Intent(
        string id, ArkadeSwapIntentStatus status, string? makerDescriptor = "wpkh(02aa)") =>
        new()
        {
            Id = id,
            WalletId = "wallet-1",
            Type = ArkadeSwapIntentType.BtcToAsset,
            OfferAmount = Money.Satoshis(50_000),
            WantAmount = Money.Satoshis(49_750),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            SwapPkScript = "5120aa",
            SwapAddress = "ark1swap",
            OfferHex = "00",
            MakerDescriptor = makerDescriptor,
        };

    [Test]
    public void CancelSwap_Throws_WhenSwapNotFound()
    {
        HaveIntents(); // storage has nothing

        Assert.That(async () => await _manager.CancelSwap("missing"),
            Throws.InvalidOperationException.With.Message.Contains("not found"));
    }

    [Test]
    public void CancelSwap_Throws_WhenNotPending()
    {
        HaveIntents(Intent("swap-1", ArkadeSwapIntentStatus.Fulfilled));

        Assert.That(async () => await _manager.CancelSwap("swap-1"),
            Throws.InvalidOperationException.With.Message.Contains("not pending"));
    }

    [Test]
    public void CancelSwap_Throws_WhenNoMakerDescriptor()
    {
        HaveIntents(Intent("swap-1", ArkadeSwapIntentStatus.Pending, makerDescriptor: null));

        Assert.That(async () => await _manager.CancelSwap("swap-1"),
            Throws.InvalidOperationException.With.Message.Contains("maker descriptor"));
    }

    [Test]
    public async Task CancelSwap_GuardFailures_NeverSaveOrSpend()
    {
        HaveIntents(Intent("swap-1", ArkadeSwapIntentStatus.Fulfilled));

        try { await _manager.CancelSwap("swap-1"); } catch (InvalidOperationException) { /* expected */ }

        // A rejected precondition must not mutate the intent or broadcast anything.
        await _intents.DidNotReceive().SaveArkadeSwapIntent(Arg.Any<ArkadeSwapIntent>(), Arg.Any<CancellationToken>());
        await _spending.DidNotReceiveWithAnyArgs().Spend(default!, default!, default!, default);
    }
}
