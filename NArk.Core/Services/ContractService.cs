using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
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

    public async Task<ArkContract> DeriveContract(
        string walletId,
        NextContractPurpose purpose,
        ContractActivityState activityState = ContractActivityState.Active,
        CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Deriving {purpose} contract for wallet {WalletId} with state {ActivityState}",
            purpose, walletId, activityState);

        var addressProvider = await walletProvider.GetAddressProviderAsync(walletId, cancellationToken);

        var (contract, entity) = await addressProvider!.GetNextContract(purpose, activityState, cancellationToken);
        await contractStorage.SaveContract(entity, cancellationToken);
        //TODO: maybe this should be in the contract storage?
        await eventHandlers.SafeHandleEventAsync(new NewContractActionEvent(contract, walletId), cancellationToken);
        logger?.LogInformation("Derived {purpose} contract for wallet {WalletId}", purpose, walletId);
        return contract;
    }

    public async Task ImportContract(
        string walletId,
        ArkContract contract,
        ContractActivityState activityState = ContractActivityState.Active,
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
        await contractStorage.SaveContract(contract.ToEntity(walletId, defaultServerKey: info.SignerKey, activityState: activityState), cancellationToken);
        await eventHandlers.SafeHandleEventAsync(new NewContractActionEvent(contract, walletId), cancellationToken);
        logger?.LogInformation("Imported contract for wallet {WalletId}", walletId);
    }

}