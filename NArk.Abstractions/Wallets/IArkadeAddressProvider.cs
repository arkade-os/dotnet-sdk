using NArk.Abstractions.Contracts;
using NBitcoin.Scripting;

namespace NArk.Abstractions.Wallets;

/// <summary>Intended use of a freshly derived Arkade contract.</summary>
public enum NextContractPurpose
{
    /// <summary>Standard receive address.</summary>
    Receive,
    /// <summary>Change output for a send-to-self within the same wallet.</summary>
    SendToSelf,
    /// <summary>On-chain boarding contract to fund the wallet from L1.</summary>
    Boarding
}

/// <summary>Derives and tracks Arkade contracts for a wallet (receive addresses, boarding scripts, change outputs).</summary>
public interface IArkadeAddressProvider
{
    /// <summary>Returns true if the given descriptor belongs to this wallet.</summary>
    Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default);
    /// <summary>Returns the next unused signing descriptor without persisting a contract or advancing the gap counter.</summary>
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
