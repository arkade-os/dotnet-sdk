namespace NArk.Core.Models.Options;

/// <summary>
/// Configuration options for VTXO polling after batch completion or transaction broadcast.
/// </summary>
public class VtxoPollingOptions
{
    /// <summary>
    /// Delay before polling VTXOs after batch success.
    /// This helps avoid race conditions where the server hasn't persisted the changes yet.
    /// Default: 500ms
    /// </summary>
    public TimeSpan BatchSuccessPollingDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Delay before polling VTXOs after transaction broadcast.
    /// Default: 500ms
    /// </summary>
    public TimeSpan TransactionBroadcastPollingDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
