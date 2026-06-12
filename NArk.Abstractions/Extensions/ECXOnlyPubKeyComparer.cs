using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Extensions;

/// <summary>
/// Value-equality comparer for <see cref="ECXOnlyPubKey"/>.
/// NBitcoin's <see cref="ECXOnlyPubKey"/> overrides <see cref="object.GetHashCode"/> by byte value
/// but not <see cref="object.Equals(object)"/>, so <c>Equals</c> falls back to reference equality.
/// Pass this comparer to any <see cref="System.Collections.Generic.HashSet{T}"/> or
/// <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> keyed on <see cref="ECXOnlyPubKey"/>.
/// </summary>
public sealed class ECXOnlyPubKeyComparer : IEqualityComparer<ECXOnlyPubKey>
{
    public static readonly ECXOnlyPubKeyComparer Instance = new();
    private ECXOnlyPubKeyComparer() { }

    public bool Equals(ECXOnlyPubKey? x, ECXOnlyPubKey? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.ToBytes().SequenceEqual(y.ToBytes());
    }

    // GetHashCode is already overridden by NBitcoin to be byte-based — reuse it.
    public int GetHashCode(ECXOnlyPubKey obj) => obj.GetHashCode();
}
