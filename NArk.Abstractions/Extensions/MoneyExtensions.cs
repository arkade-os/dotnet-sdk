using NBitcoin;

namespace NArk.Abstractions.Extensions;

/// <summary>
/// LINQ helpers that keep satoshi amounts in <see cref="Money"/>.
/// </summary>
public static class MoneyExtensions
{
    /// <summary>
    /// Sums a projected <see cref="Money"/> amount.
    /// </summary>
    /// <remarks>
    /// Without this overload, <c>coins.Sum(c =&gt; c.Amount)</c> binds to
    /// <see cref="Enumerable.Sum{TSource}(IEnumerable{TSource}, Func{TSource, long})"/> —
    /// NBitcoin's <see cref="Money"/> converts implicitly to <see cref="long"/>, so the
    /// total silently drops back to a bare satoshi count and every downstream comparison
    /// loses its unit. An exact <see cref="Money"/> match wins overload resolution over that
    /// implicit conversion, so simply having this in scope keeps the sum typed.
    /// </remarks>
    public static Money Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, Money> selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);

        var total = Money.Zero;
        foreach (var item in source)
            total += selector(item);
        return total;
    }
}
