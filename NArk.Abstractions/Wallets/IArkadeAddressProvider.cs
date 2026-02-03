using NArk.Abstractions.Contracts;
using NBitcoin.Scripting;

namespace NArk.Abstractions.Wallets;

public enum NextContractPurpose
{
    Receive,
    SendToSelf
}

public interface IArkadeAddressProvider
{
    Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default);
    Task<OutputDescriptor> GetNextSigningDescriptor(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the next contract for the specified purpose.
    /// </summary>
    /// <param name="purpose">Purpose of the contract</param>
    /// <param name="activityState">Activity state for the contract</param>
    /// <param name="inputContracts">Optional input contracts for descriptor recycling (SendToSelf only).
    /// When provided, HD wallets may reuse a descriptor from the inputs to avoid index bloat.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>
    /// A tuple of (contract, entity).
    /// The entity's activity state may be overridden for special cases (e.g., static sweep addresses).
    /// </returns>
    Task<(ArkContract contract, ArkContractEntity entity)> GetNextContract(
        NextContractPurpose purpose,
        ContractActivityState activityState,
        ArkContract[]? inputContracts = null,
        CancellationToken cancellationToken = default);
}
