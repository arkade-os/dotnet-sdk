using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;

namespace NArk.Core.Services;

public interface IContractService
{
    /// <summary>
    /// Derives a new contract for the specified purpose.
    /// </summary>
    Task<ArkContract> DeriveContract(
        string walletId,
        NextContractPurpose purpose,
        ContractActivityState activityState = ContractActivityState.Active,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Derives a new contract for the specified purpose, with optional input contracts for descriptor recycling.
    /// When inputContracts are provided and purpose is SendToSelf, HD wallets may reuse a descriptor
    /// from the inputs to avoid index bloat and reduce contract data accumulation.
    /// </summary>
    Task<ArkContract> DeriveContract(
        string walletId,
        NextContractPurpose purpose,
        ArkContract[] inputContracts,
        ContractActivityState activityState = ContractActivityState.Active,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    Task ImportContract(
        string walletId,
        ArkContract contract,
        ContractActivityState activityState = ContractActivityState.Active,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);
}
