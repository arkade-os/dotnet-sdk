namespace NArk.Abstractions.Wallets;

/// <summary>
/// Wallet information record used at the abstraction/interface boundary.
/// </summary>
public record ArkWalletInfo(
    string Id,
    string Secret,
    string? Destination,
    WalletType WalletType,
    string? AccountDescriptor,
    int LastUsedIndex);
