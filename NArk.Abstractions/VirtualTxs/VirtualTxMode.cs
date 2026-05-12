namespace NArk.Abstractions.VirtualTxs;

/// <summary>
/// Controls how much virtual tx data is stored.
/// </summary>
public enum VirtualTxMode
{
    /// <summary>
    /// Store only txids and branch structure. Tx hex is fetched on demand when needed for exit.
    /// </summary>
    Lite = 0,

    /// <summary>
    /// Store txids AND raw tx hex immediately on VTXO receive. Ready for instant exit.
    /// </summary>
    Full = 1
}
