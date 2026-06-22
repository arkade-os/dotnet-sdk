namespace NArk.Abstractions.Wallets;

/// <summary>Raised when a wallet's sweep destination is auto-disabled because an Arkade signer rotation made it stale.</summary>
public sealed class DestinationDisabledEventArgs : EventArgs
{
    /// <summary>Wallet whose sweep destination was disabled.</summary>
    public required string WalletId { get; init; }
    /// <summary>The (now stale) destination address string that was disabled.</summary>
    public required string Destination { get; init; }
    /// <summary>The deprecated server key (hex) the destination was pinned to.</summary>
    public required string DeprecatedServerKey { get; init; }
}

/// <summary>
/// Notifies consumers (e.g. a BTCPay plugin) that a wallet's sweep destination was disabled pending
/// re-confirmation after an Arkade signer rotation. DI-aliased to the same singleton that performs detection.
/// </summary>
public interface IDestinationSafetyNotifier
{
    /// <summary>Raised when a wallet's sweep destination is auto-disabled due to an Arkade signer rotation.</summary>
    event EventHandler<DestinationDisabledEventArgs>? DestinationDisabled;
}
