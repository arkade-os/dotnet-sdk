using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Events;
using NArk.Core.Transport;
using NArk.Core.Extensions;

namespace NArk.Core.Services;

public class ContractService(
    IWalletProvider walletProvider,
    IContractStorage contractStorage,
    IClientTransport transport,
    IEnumerable<IEventHandler<NewContractActionEvent>> eventHandlers,
    ILogger<ContractService>? logger = null) : IContractService
{
    public ContractService(IWalletProvider walletProvider,
        IContractStorage contractStorage,
        IClientTransport transport) : this(walletProvider, contractStorage, transport, [], null)
    {
    }

    public ContractService(IWalletProvider walletProvider,
        IContractStorage contractStorage,
        IClientTransport transport,
        ILogger<ContractService> logger) : this(walletProvider, contractStorage, transport, [], logger)
    {
    }

    public Task<ArkContract> DeriveContract(
        string walletId,
        NextContractPurpose purpose,
        ContractActivityState activityState = ContractActivityState.Active,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return DeriveContractInternal(walletId, purpose, null, activityState, metadata, cancellationToken);
    }

    public Task<ArkContract> DeriveContract(
        string walletId,
        NextContractPurpose purpose,
        ArkContract[] inputContracts,
        ContractActivityState activityState = ContractActivityState.Active,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return DeriveContractInternal(walletId, purpose, inputContracts, activityState, metadata, cancellationToken);
    }

    private async Task<ArkContract> DeriveContractInternal(
        string walletId,
        NextContractPurpose purpose,
        ArkContract[]? inputContracts,
        ContractActivityState activityState,
        Dictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        logger?.LogDebug("Deriving {Purpose} contract for wallet {WalletId} with state {ActivityState}, inputContracts: {InputCount}",
            purpose, walletId, activityState, inputContracts?.Length ?? 0);

        var addressProvider = await walletProvider.GetAddressProviderAsync(walletId, cancellationToken);

        var (contract, entity) = await addressProvider!.GetNextContract(purpose, activityState, inputContracts, cancellationToken);

        if (metadata is { Count: > 0 })
            entity = entity with { Metadata = metadata };

        await contractStorage.SaveContract(entity, cancellationToken);

        await eventHandlers.SafeHandleEventAsync(new NewContractActionEvent(contract, walletId), cancellationToken);
        logger?.LogInformation("Derived {Purpose} contract for wallet {WalletId}", purpose, walletId);
        return contract;
    }

    public async Task ImportContract(
        string walletId,
        ArkContract contract,
        ContractActivityState activityState = ContractActivityState.Active,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Importing contract for wallet {WalletId} with state {ActivityState}",
            walletId, activityState);
        var info = await transport.GetServerInfoAsync(cancellationToken);
        if (contract.Server is not null && !contract.Server.Equals(info.SignerKey))
        {
            logger?.LogWarning("Cannot import contract for wallet {WalletId}: server key mismatch", walletId);
            throw new InvalidOperationException("Cannot import contract with different server key");
        }
        var entity = contract.ToEntity(walletId, defaultServerKey: info.SignerKey, activityState: activityState);
        if (metadata is { Count: > 0 })
            entity = entity with { Metadata = metadata };
        await contractStorage.SaveContract(entity, cancellationToken);
        await eventHandlers.SafeHandleEventAsync(new NewContractActionEvent(contract, walletId), cancellationToken);
        logger?.LogInformation("Imported contract for wallet {WalletId}", walletId);
    }
}
