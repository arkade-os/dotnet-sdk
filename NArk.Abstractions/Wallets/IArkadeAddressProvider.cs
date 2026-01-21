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
    /// <param name="activityState"></param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>
    /// A tuple of (contract, suggestedActivityState).
    /// If suggestedActivityState is non-null, it overrides any caller-provided state (used for reusable/static addresses).
    /// </returns>
    Task<(ArkContract contract, ArkContractEntity entity)> GetNextContract(
        NextContractPurpose purpose,
        ContractActivityState activityState,
        CancellationToken cancellationToken = default);
}