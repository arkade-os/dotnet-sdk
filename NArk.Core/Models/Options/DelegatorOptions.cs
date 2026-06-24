using NBitcoin.Scripting;

namespace NArk.Core.Models.Options;

/// <summary>
/// Configuration for the Arkade delegator (the server side of the VTXO-refresh delegation service).
/// </summary>
public class DelegatorOptions
{
    /// <summary>Wallet identifier whose signer the delegator co-signs and joins batches with.</summary>
    public required string WalletId { get; set; }

    /// <summary>
    /// The delegator's signing descriptor. Its key appears in the delegate leaf of each delegate
    /// contract, and is what clients embed after calling GetDelegatorInfo.
    /// </summary>
    public required OutputDescriptor DelegateDescriptor { get; set; }

    /// <summary>Service fee advertised via GetDelegatorInfo, as a plain-text amount string (sats).</summary>
    public string Fee { get; set; } = "0";

    /// <summary>The Arkade address the service fee is paid to. Empty when no fee is charged.</summary>
    public string DelegatorAddress { get; set; } = "";

    /// <summary>How long before a VTXO's expiry the refresh batch should fire (time-based).</summary>
    public TimeSpan RefreshThreshold { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How many blocks before a VTXO's height-expiry the refresh should fire.</summary>
    public uint RefreshThresholdHeight { get; set; } = 144;
}
