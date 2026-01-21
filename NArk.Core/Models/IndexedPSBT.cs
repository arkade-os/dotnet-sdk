using NBitcoin;

namespace NArk.Core.Models;

public record IndexedPSBT(PSBT Psbt, int Index) : IComparable
{
    public int CompareTo(object? obj)
    {
        if (obj is not IndexedPSBT other) return -1;
        return Index.CompareTo(other.Index);
    }
}