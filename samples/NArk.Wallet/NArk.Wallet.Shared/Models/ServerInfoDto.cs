namespace NArk.Wallet.Shared.Models;

public record ServerInfoDto(
    string Network,
    string ServerUrl,
    int BatchIntervalSeconds,
    long DustSats);
