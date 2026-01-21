using NArk.Abstractions.Contracts;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemoryContractStorage : IContractStorage
{
    private readonly Dictionary<string, HashSet<ArkContractEntity>> _contracts = new();

    public event EventHandler<ArkContractEntity>? ContractsChanged;
    public event EventHandler? ActiveScriptsChanged;

    public Task<IReadOnlySet<ArkContractEntity>> LoadAllContractsByWallet(string walletIdentifier,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            return Task.FromResult<IReadOnlySet<ArkContractEntity>>(
                _contracts.TryGetValue(walletIdentifier, out var contracts) ? contracts : []);
        }
    }

    public Task<IReadOnlySet<ArkContractEntity>> LoadActiveContracts(IReadOnlyCollection<string>? walletIdentifiers = null,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
            return Task.FromResult<IReadOnlySet<ArkContractEntity>>(_contracts
                .Where(x => walletIdentifiers is null || walletIdentifiers.Contains(x.Key))
                .SelectMany(x => x.Value)
                .Where(x => x.ActivityState != ContractActivityState.Inactive)
                .ToHashSet());
    }

    public Task<IReadOnlySet<ArkContractEntity>> LoadContractsByScripts(string[] scripts, IReadOnlyCollection<string>? walletIdentifiers = null,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            if (walletIdentifiers is null)
                return Task.FromResult<IReadOnlySet<ArkContractEntity>>(_contracts.Values.SelectMany(x => x).Where(x => scripts.Contains(x.Script)).ToHashSet());

            var walletContracts = _contracts.Where(x => walletIdentifiers.Contains(x.Key)).ToDictionary(k => k.Key, v => v.Value);
            return Task.FromResult<IReadOnlySet<ArkContractEntity>>(walletContracts.SelectMany(x => x.Value).Where(x => scripts.Contains(x.Script)).ToHashSet());
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