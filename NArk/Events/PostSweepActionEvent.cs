using NArk.Abstractions;
using NArk.Enums;
using NBitcoin;

namespace NArk.Events;

public record PostSweepActionEvent(
    ArkCoin ArkCoin,
    uint256? TransactionId,
    ActionState State,
    string? FailReason
);
