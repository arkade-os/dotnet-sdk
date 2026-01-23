using NArk.Abstractions;
using NArk.Core.Enums;
using NBitcoin;

namespace NArk.Core.Events;

public record PostSweepActionEvent(
    ArkCoin ArkCoin,
    uint256? TransactionId,
    ActionState State,
    string? FailReason
);
