namespace NArk.Core.CoinSelector;

/// <summary>
/// Thrown when a spend cannot be funded without exceeding the maximum number of
/// VTXO inputs allowed in a single Arkade transaction. The wallet holds enough
/// funds overall, but they are fragmented across too many small VTXOs — wait for
/// the intent scheduler to consolidate them, or send a smaller amount.
/// </summary>
public class TooManyInputsException(int maxInputs)
    : Exception(
        $"Funding this spend requires more than {maxInputs} VTXO inputs; the Arkade server would reject the transaction as too large (TX_TOO_LARGE). Consolidate VTXOs first or send a smaller amount.")
{
    /// <summary>The input cap that could not be satisfied.</summary>
    public int MaxInputs { get; } = maxInputs;
}
