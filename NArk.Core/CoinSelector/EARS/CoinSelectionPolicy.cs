namespace NArk.Core.CoinSelector.EARCoinSelector;

public sealed record CoinSelectionPolicy(
    bool ExpiryFirst = true,
    bool PreferNoChange = true,
    bool PreferLowFee = true,
    bool AllowExpiryMixingFallback = true,
    bool AllowRandomization = true,
    int RandomTopK = 3,
    int MaxBnBInputs = 12,
    int MaxLocalSearchIterations = 50,
    uint ExpiryWindowBlocks = 144, // ~24h: batches expiring within this window are grouped together
    long CostPerInputSats = 68);   // flat per-input cost added to waste (default: 1 sat/vbyte × ~68 vbyte input)