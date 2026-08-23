namespace NArk.Abstractions.Intents;

/// <summary>
/// The inputs and outputs of a pending Arkade intent before it is registered with the server.
/// </summary>
public record ArkIntentSpec(
    ArkCoin[] Coins,
    ArkTxOut[] Outputs,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil
);
