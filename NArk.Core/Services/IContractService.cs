using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;

namespace NArk.Core.Services;

public interface IContractService
{
    Task<ArkContract> DeriveContract(
        string walletId,
        NextContractPurpose purpose,
        ContractActivityState activityState = ContractActivityState.Active,
        CancellationToken cancellationToken = default);

    Task ImportContract(
        string walletId,
        ArkContract contract,
        ContractActivityState activityState = ContractActivityState.Active,
        CancellationToken cancellationToken = default);
}