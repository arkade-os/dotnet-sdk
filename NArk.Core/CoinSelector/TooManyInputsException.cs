namespace NArk.Core.CoinSelector;

/// <summary>
/// Thrown when a spend cannot be funded without exceeding the Arkade server's
/// <c>max_tx_weight</c> limit. The wallet holds enough funds overall, but they
/// are fragmented across too many small VTXOs whose combined input weight would
/// breach the limit — wait for the intent scheduler to consolidate them, or
/// send a smaller amount.
/// </summary>
public class TooManyInputsException(long maxInputWeightWu)
    : Exception(
        $"Funding this spend requires inputs whose combined weight exceeds {maxInputWeightWu} WU; the Arkade server would reject the transaction as too large (TX_TOO_LARGE). Consolidate VTXOs first or send a smaller amount.")
{
    /// <summary>The input weight budget (in weight units) that could not be satisfied.</summary>
    public long MaxInputWeightWu { get; } = maxInputWeightWu;
}
