using NArk.Abstractions.Settlement;
using NArk.Core.Enums;

namespace NArk.Core.Events;

/// <summary>
/// Raised after the settlement engine attempted a settlement, whether it succeeded or failed.
/// Register an <see cref="IEventHandler{TEvent}"/> for it to record transfers, notify a user,
/// or drive a retry of your own.
/// </summary>
/// <param name="Request">The settlement that was attempted.</param>
/// <param name="Result">The outcome when <paramref name="State"/> is successful; otherwise <see langword="null"/>.</param>
/// <param name="State">Whether the settlement succeeded.</param>
/// <param name="FailReason">Failure message when the settlement failed; otherwise <see langword="null"/>.</param>
public record PostSettlementActionEvent(
    SettlementRequest Request,
    SettlementResult? Result,
    ActionState State,
    string? FailReason);
