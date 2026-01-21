using NArk.Abstractions;
using NArk.Enums;
using NBitcoin;

namespace NArk.Events;

public record PostCoinsSpendActionEvent(
    IReadOnlyCollection<ArkCoin> ArkCoins,
    uint256? TransactionId,
    ActionState State,
    string? FailReason
);