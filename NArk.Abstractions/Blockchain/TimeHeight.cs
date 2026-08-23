namespace NArk.Abstractions.Blockchain;

/// <summary>Current chain wall-clock time and block height, used for expiry and CSV-maturity checks.</summary>
public record TimeHeight(DateTimeOffset Timestamp, uint Height);
