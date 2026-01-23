using NArk.Abstractions;
using NArk.Core.Enums;
using NBitcoin;

namespace NArk.Core.Events;

public record PostCoinsSpendActionEvent(
    IReadOnlyCollection<ArkCoin> ArkCoins,
    uint256? TransactionId,
    ActionState State,
    string? FailReason
);