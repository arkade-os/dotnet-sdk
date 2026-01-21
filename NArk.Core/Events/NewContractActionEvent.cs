using NArk.Abstractions.Contracts;

namespace NArk.Core.Events;

public record NewContractActionEvent(
    ArkContract Contract,
    string WalletId
);