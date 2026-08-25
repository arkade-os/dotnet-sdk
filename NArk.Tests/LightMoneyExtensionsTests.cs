using BTCPayServer.Lightning;
using NArk.Swaps.Extensions;
using NBitcoin;

namespace NArk.Tests;

/// <summary>
/// Tests for <see cref="LightMoneyExtensions"/> — the msat→sat boundary every swap amount
/// crosses. Truncating here pins a swap below the invoice it is meant to settle.
/// </summary>
[TestFixture]
public class LightMoneyExtensionsTests
{
    [TestCase(0L, 0L)]
    [TestCase(1_000L, 1L)]
    [TestCase(1L, 1L)]
    [TestCase(999L, 1L)]
    [TestCase(1_001L, 2L)]
    [TestCase(1_000_500L, 1_001L)]
    [TestCase(50_000_000L, 50_000L)]
    public void ToSatoshisRoundingUp_RoundsAnyRemainderUp(long milliSatoshi, long expectedSatoshi)
    {
        var amount = LightMoney.MilliSatoshis(milliSatoshi);

        Assert.That(amount.ToSatoshisRoundingUp(), Is.EqualTo(Money.Satoshis(expectedSatoshi)));
    }
}
