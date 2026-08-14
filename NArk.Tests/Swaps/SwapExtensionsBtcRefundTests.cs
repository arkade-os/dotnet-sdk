using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Extensions;
using NArk.Core.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.Scripting;
using NSubstitute;

namespace NArk.Tests.Swaps;

/// <summary>
/// Regression coverage for the BTC→ARK chain-swap refund-destination bug: refunds must land
/// on a freshly-derived, wallet-owned on-chain (boarding) address, never back on the swap's
/// own BTC lockup/HTLC address. See <see cref="SwapExtensions.GetOrDeriveBtcRefundDestinationAsync"/>.
/// </summary>
[TestFixture]
public class SwapExtensionsBtcRefundTests
{
    private static readonly OutputDescriptor ServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly OutputDescriptor UserKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            Network.RegTest);

    private static readonly Sequence ExitDelay = new(144);

    // Stand-in for the swap's own BTC lockup/HTLC address (as stored under
    // SwapMetadata.BtcAddress for ChainBtcToArk swaps) — the wrong destination the bug used.
    private const string LockupAddress = "bcrt1pfqecg30q3nnzr4zh2xsxytwjw6zvmvhxwjqu5vxwkr9zktcwqp3sfr9d4z";

    private static ArkSwap MakeSwap(Dictionary<string, string>? metadata = null) =>
        new("swap-1", "wallet-1", ArkSwapType.ChainBtcToArk, "", 50_000,
            "contract-script", LockupAddress, ArkSwapStatus.Pending, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "hash")
        {
            Metadata = metadata
        };

    [Test]
    public async Task DerivesBoardingAddress_NotTheLockupAddress_AndCachesIt()
    {
        var swap = MakeSwap();
        var boardingContract = new ArkBoardingContract(ServerKey, ExitDelay, UserKey);

        var contractService = Substitute.For<IContractService>();
        contractService.DeriveContract(
                swap.WalletId,
                NextContractPurpose.Boarding,
                ContractActivityState.AwaitingFundsBeforeDeactivate,
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ArkContract>(boardingContract));

        ArkSwap? savedSwap = null;
        var swapStorage = Substitute.For<ISwapStorage>();
        swapStorage.SaveSwap(Arg.Any<string>(), Arg.Do<ArkSwap>(s => savedSwap = s), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var (destination, updatedSwap) = await swap.GetOrDeriveBtcRefundDestinationAsync(
            contractService, swapStorage, Network.RegTest, CancellationToken.None);

        var expectedAddress = boardingContract.GetOnchainAddress(Network.RegTest);

        Assert.That(destination, Is.EqualTo(expectedAddress));
        // The whole point of the fix: never refund back to the swap's own lockup script.
        Assert.That(destination.ToString(), Is.Not.EqualTo(LockupAddress));

        Assert.That(savedSwap, Is.Not.Null);
        Assert.That(savedSwap!.Metadata?[SwapMetadata.BtcRefundAddress], Is.EqualTo(expectedAddress.ToString()));
        Assert.That(updatedSwap.Metadata?[SwapMetadata.BtcRefundAddress], Is.EqualTo(expectedAddress.ToString()));

        await contractService.Received(1).DeriveContract(
            swap.WalletId, NextContractPurpose.Boarding,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReusesCachedAddress_WithoutDerivingANewContract()
    {
        var boardingContract = new ArkBoardingContract(ServerKey, ExitDelay, UserKey);
        var cachedAddress = boardingContract.GetOnchainAddress(Network.RegTest).ToString();

        var swap = MakeSwap(new Dictionary<string, string>
        {
            [SwapMetadata.BtcRefundAddress] = cachedAddress
        });

        var contractService = Substitute.For<IContractService>();
        var swapStorage = Substitute.For<ISwapStorage>();

        var (destination, updatedSwap) = await swap.GetOrDeriveBtcRefundDestinationAsync(
            contractService, swapStorage, Network.RegTest, CancellationToken.None);

        Assert.That(destination.ToString(), Is.EqualTo(cachedAddress));
        Assert.That(updatedSwap, Is.SameAs(swap));

        await contractService.DidNotReceive().DeriveContract(
            Arg.Any<string>(), Arg.Any<NextContractPurpose>(), Arg.Any<ContractActivityState>(),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
        await swapStorage.DidNotReceive().SaveSwap(
            Arg.Any<string>(), Arg.Any<ArkSwap>(), Arg.Any<CancellationToken>());
    }
}
