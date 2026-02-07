namespace NArk.Abstractions.Intents;

public record ArkIntentSpec(
    ArkCoin[] Coins,
    ArkTxOut[] Outputs,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil
);