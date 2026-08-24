using NArk.Abstractions.Extensions;
using NBitcoin;

namespace NArk.Tests;

/// <summary>
/// Tests for <see cref="MoneyExtensions"/>. The point of the overload is overload resolution
/// itself: <see cref="Money"/> converts implicitly to <see cref="long"/>, so without it a sum
/// over money silently comes back as a bare satoshi count.
/// </summary>
[TestFixture]
public class MoneyExtensionsTests
{
    private record Holder(Money Amount);

    [Test]
    public void Sum_OverMoneySelector_StaysTyped()
    {
        Holder[] holders = [new(Money.Satoshis(1_000)), new(Money.Satoshis(2_500))];

        var total = holders.Sum(h => h.Amount);

        Assert.That(total, Is.TypeOf<Money>());
        Assert.That(total, Is.EqualTo(Money.Satoshis(3_500)));
    }

    [Test]
    public void Sum_OverEmptySequence_IsZero()
    {
        var total = Array.Empty<Holder>().Sum(h => h.Amount);

        Assert.That(total, Is.EqualTo(Money.Zero));
    }

    [Test]
    public void Sum_HandlesNegativeAmounts()
    {
        Holder[] holders = [new(Money.Satoshis(1_000)), new(Money.Satoshis(-400))];

        Assert.That(holders.Sum(h => h.Amount), Is.EqualTo(Money.Satoshis(600)));
    }
}
