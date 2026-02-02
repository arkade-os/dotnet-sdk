using NArk.Abstractions.Scripts;

namespace NArk.Abstractions.Contracts;

public interface IContractStorage : IActiveScriptsProvider
{
    event EventHandler<ArkContractEntity>? ContractsChanged;

    /// <summary>
    /// Query contracts with explicit filter parameters.
    /// Adding new parameters will cause compile errors for implementors, ensuring they handle new filters.
    /// </summary>
    /// <param name="walletIds">Filter by wallet IDs. If null, all wallets.</param>
    /// <param name="scripts">Filter by script hex strings. If null, no script filter.</param>
    /// <param name="isActive">If true, only active (not Inactive); if false, only Inactive; if null, all.</param>
    /// <param name="contractTypes">Filter by contract types. If null, no type filter.</param>
    /// <param name="searchText">Filter by script containing this text. If null, no text filter.</param>
    /// <param name="skip">Number of records to skip (for pagination). If null, no skip.</param>
    /// <param name="take">Number of records to take (for pagination). If null, no limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyCollection<ArkContractEntity>> GetContracts(
        string[]? walletIds = null,
        string[]? scripts = null,
        bool? isActive = null,
        string[]? contractTypes = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default);

    Task SaveContract(ArkContractEntity walletEntity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the activity state of a contract.
    /// </summary>
    /// <param name="walletId">The wallet ID that owns the contract</param>
    /// <param name="script">The script hex of the contract</param>
    /// <param name="activityState">The new activity state</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the contract was found and updated, false otherwise</returns>
    Task<bool> UpdateContractActivityState(
        string walletId,
        string script,
        ContractActivityState activityState,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a contract by script. Wallet ID guards ownership.
    /// </summary>
    /// <param name="walletId">The wallet ID that owns the contract</param>
    /// <param name="script">The script hex of the contract to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the contract was deleted, false if not found</returns>
    Task<bool> DeleteContract(
        string walletId,
        string script,
        CancellationToken cancellationToken = default);

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
        return (await GetContracts(isActive: true, cancellationToken: cancellationToken)).Select(c => c.Script).ToHashSet();
    }
}