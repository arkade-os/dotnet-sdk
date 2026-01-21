using NArk.Abstractions.Scripts;

namespace NArk.Abstractions.Contracts;

public interface IContractStorage : IActiveScriptsProvider
{
    event EventHandler<ArkContractEntity>? ContractsChanged;
    Task<IReadOnlySet<ArkContractEntity>> LoadAllContractsByWallet(string walletIdentifier, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<ArkContractEntity>> LoadActiveContracts(IReadOnlyCollection<string>? walletIdentifiers = null, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<ArkContractEntity>> LoadContractsByScripts(string[] scripts, IReadOnlyCollection<string>? walletIdentifiers = null, CancellationToken cancellationToken = default);
    Task SaveContract(ArkContractEntity walletEntity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates all contracts with the given script that are in AwaitingFundsBeforeDeactivate state.
    /// Called when a VTXO is received to auto-deactivate one-time-use contracts (like refund addresses).
    /// </summary>
    /// <param name="script">The script hex to match against contracts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of contracts deactivated</returns>
    Task<int> DeactivateAwaitingContractsByScript(string script, CancellationToken cancellationToken = default);

    async Task<HashSet<string>> IActiveScriptsProvider.GetActiveScripts(CancellationToken cancellationToken)
    {
        return (await LoadActiveContracts(null, cancellationToken)).Select(c => c.Script).ToHashSet();
    }
}