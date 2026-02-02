using NArk.Abstractions.Contracts;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemoryContractStorage : IContractStorage
{
    private readonly Dictionary<string, HashSet<ArkContractEntity>> _contracts = new();

    public event EventHandler<ArkContractEntity>? ContractsChanged;
    public event EventHandler? ActiveScriptsChanged;

    public Task<IReadOnlyCollection<ArkContractEntity>> GetContracts(
        string[]? walletIds = null,
        string[]? scripts = null,
        bool? isActive = null,
        string[]? contractTypes = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            IEnumerable<ArkContractEntity> query = _contracts.Values.SelectMany(x => x);

            // Filter by wallet IDs
            if (walletIds is { Length: > 0 })
            {
                var walletSet = walletIds.ToHashSet();
                query = query.Where(c => walletSet.Contains(c.WalletIdentifier));
            }

            // Filter by scripts
            if (scripts is { Length: > 0 })
            {
                var scriptSet = scripts.ToHashSet();
                query = query.Where(c => scriptSet.Contains(c.Script));
            }

            // Filter by activity state
            if (isActive.HasValue)
            {
                query = isActive.Value
                    ? query.Where(c => c.ActivityState != ContractActivityState.Inactive)
                    : query.Where(c => c.ActivityState == ContractActivityState.Inactive);
            }

            // Filter by contract types
            if (contractTypes is { Length: > 0 })
            {
                var typeSet = contractTypes.ToHashSet();
                query = query.Where(c => typeSet.Contains(c.Type));
            }

            // Filter by search text (script contains)
            if (!string.IsNullOrEmpty(searchText))
            {
                query = query.Where(c => c.Script.Contains(searchText, StringComparison.OrdinalIgnoreCase));
            }

            // Pagination
            if (skip.HasValue)
            {
                query = query.Skip(skip.Value);
            }

            if (take.HasValue)
            {
                query = query.Take(take.Value);
            }

            return Task.FromResult<IReadOnlyCollection<ArkContractEntity>>(query.ToList());
        }
    }

    public Task<bool> UpdateContractActivityState(
        string walletId,
        string script,
        ContractActivityState activityState,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            if (!_contracts.TryGetValue(walletId, out var contracts))
                return Task.FromResult(false);

            var existing = contracts.FirstOrDefault(c => c.Script == script);
            if (existing == null)
                return Task.FromResult(false);

            contracts.Remove(existing);
            var updated = existing with { ActivityState = activityState };
            contracts.Add(updated);

            ContractsChanged?.Invoke(this, updated);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);

            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteContract(
        string walletId,
        string script,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            if (!_contracts.TryGetValue(walletId, out var contracts))
                return Task.FromResult(false);

            var existing = contracts.FirstOrDefault(c => c.Script == script);
            if (existing == null)
                return Task.FromResult(false);

            contracts.Remove(existing);

            ContractsChanged?.Invoke(this, existing);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);

            return Task.FromResult(true);
        }
    }

    public Task SaveContract(ArkContractEntity contractEntity,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            if (_contracts.TryGetValue(contractEntity.WalletIdentifier, out var contracts))
                contracts.Add(contractEntity);
            else
                _contracts[contractEntity.WalletIdentifier] = [contractEntity];
            ContractsChanged?.Invoke(this, contractEntity);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    public Task<int> DeactivateAwaitingContractsByScript(string script, CancellationToken cancellationToken = default)
    {
        var deactivatedCount = 0;
        lock (_contracts)
        {
            foreach (var walletContracts in _contracts.Values)
            {
                var awaitingContracts = walletContracts
                    .Where(c => c.Script == script && c.ActivityState == ContractActivityState.AwaitingFundsBeforeDeactivate)
                    .ToList();

                foreach (var contract in awaitingContracts)
                {
                    walletContracts.Remove(contract);
                    var deactivatedContract = contract with { ActivityState = ContractActivityState.Inactive };
                    walletContracts.Add(deactivatedContract);
                    ContractsChanged?.Invoke(this, deactivatedContract);
                    deactivatedCount++;
                }
            }

            if (deactivatedCount > 0)
            {
                ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        return Task.FromResult(deactivatedCount);
    }
}