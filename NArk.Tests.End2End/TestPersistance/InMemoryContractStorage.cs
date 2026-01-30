using NArk.Abstractions.Contracts;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemoryContractStorage : IContractStorage
{
    private readonly Dictionary<string, HashSet<ArkContractEntity>> _contracts = new();

    public event EventHandler<ArkContractEntity>? ContractsChanged;
    public event EventHandler? ActiveScriptsChanged;

    public Task<IReadOnlySet<ArkContractEntity>> GetContracts(
        string[]? walletIds = null,
        string[]? scripts = null,
        bool? isActive = null,
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

            // Pagination
            if (skip.HasValue)
            {
                query = query.Skip(skip.Value);
            }

            if (take.HasValue)
            {
                query = query.Take(take.Value);
            }

            return Task.FromResult<IReadOnlySet<ArkContractEntity>>(query.ToHashSet());
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