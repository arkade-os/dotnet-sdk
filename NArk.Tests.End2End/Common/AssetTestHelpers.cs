using NArk.Abstractions.Assets;
using NArk.Abstractions.Wallets;
using NArk.Core.CoinSelector;
using NArk.Core.Services;
using NArk.Core.Transformers;
using NArk.Core.Transport;
using NArk.Safety.AsyncKeyedLock;
using NArk.Tests.End2End.TestPersistance;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Shared asset test helpers reused by AssetTests (Core namespace) and DelegationTests (Delegation namespace).
/// </summary>
internal static class AssetTestHelpers
{
    internal static (AssetManager assetManager, CoinService coinService, InMemoryIntentStorage intentStorage)
        CreateAssetServices(
            (AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
                string walletIdentifier, InMemoryVtxoStorage vtxoStorage,
                ContractService contractService, InMemoryContractStorage contracts,
                IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) walletDetails,
            IContractTransformer[]? additionalTransformers = null)
    {
        var transformers = new List<IContractTransformer>
        {
            new PaymentContractTransformer(walletDetails.walletProvider),
            new HashLockedContractTransformer(walletDetails.walletProvider)
        };
        if (additionalTransformers is not null)
            transformers.AddRange(additionalTransformers);

        var coinService = new CoinService(walletDetails.clientTransport, walletDetails.contracts,
            transformers.ToArray());

        var intentStorage = new InMemoryIntentStorage();

        var assetManager = new AssetManager(
            walletDetails.vtxoStorage,
            walletDetails.contracts,
            coinService,
            walletDetails.walletProvider,
            walletDetails.contractService,
            walletDetails.clientTransport,
            new DefaultCoinSelector(),
            walletDetails.safetyService,
            intentStorage,
            []);

        return (assetManager, coinService, intentStorage);
    }

    internal static async Task PollAllScripts(
        (AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier, InMemoryVtxoStorage vtxoStorage,
            ContractService contractService, InMemoryContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) walletDetails)
    {
        await Task.Delay(500);
        var allContracts = await walletDetails.contracts.GetContracts(
            walletIds: [walletDetails.walletIdentifier]);
        foreach (var contract in allContracts)
        {
            await walletDetails.vtxoSync.PollScriptsForVtxos(
                new HashSet<string> { contract.Script });
        }
    }

    internal static async Task PollUntilAssetVtxo(
        (AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier, InMemoryVtxoStorage vtxoStorage,
            ContractService contractService, InMemoryContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) walletDetails,
        string assetId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var vtxos = await walletDetails.vtxoStorage.GetVtxos(includeSpent: false);
            if (vtxos.Any(v => v.Assets is { Count: > 0 } assets &&
                               assets.Any(a => a.AssetId == assetId)))
                return;

            var allContracts = await walletDetails.contracts.GetContracts(
                walletIds: [walletDetails.walletIdentifier]);
            foreach (var contract in allContracts)
            {
                await walletDetails.vtxoSync.PollScriptsForVtxos(
                    new HashSet<string> { contract.Script });
            }

            await Task.Delay(1000);
        }

        var finalVtxos = await walletDetails.vtxoStorage.GetVtxos(includeSpent: false);
        var vtxoInfo = string.Join("; ", finalVtxos.Select(v =>
            $"txid={v.TransactionId}:{v.TransactionOutputIndex} amount={v.Amount} script={v.Script[..Math.Min(20, v.Script.Length)]}... " +
            $"assets=[{(v.Assets != null ? string.Join(",", v.Assets.Select(a => $"{a.AssetId}:{a.Amount}")) : "none")}]"));
        var diagContracts = await walletDetails.contracts.GetContracts(
            walletIds: [walletDetails.walletIdentifier]);
        var contractInfo = string.Join("; ", diagContracts.Select(c =>
            $"script={c.Script[..Math.Min(20, c.Script.Length)]}... state={c.ActivityState}"));
        throw new TimeoutException(
            $"Timed out waiting for asset VTXO with assetId={assetId}. " +
            $"VTXOs in storage: [{vtxoInfo}]. " +
            $"Contracts: [{contractInfo}]");
    }

    internal static async Task PollUntilAssetBalance(
        (AsyncSafetyService safetyService, InMemoryWalletProvider walletProvider,
            string walletIdentifier, InMemoryVtxoStorage vtxoStorage,
            ContractService contractService, InMemoryContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) walletDetails,
        string assetId, ulong expectedBalance, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var balance = await GetAssetBalance(walletDetails.vtxoStorage, assetId);
            if (balance == expectedBalance)
                return;

            var allContracts = await walletDetails.contracts.GetContracts(
                walletIds: [walletDetails.walletIdentifier]);
            foreach (var contract in allContracts)
            {
                await walletDetails.vtxoSync.PollScriptsForVtxos(
                    new HashSet<string> { contract.Script });
            }

            await Task.Delay(1000);
        }

        var finalBalance = await GetAssetBalance(walletDetails.vtxoStorage, assetId);
        throw new TimeoutException(
            $"Timed out waiting for asset balance. Expected={expectedBalance}, Actual={finalBalance}, AssetId={assetId}");
    }

    internal static async Task<ulong> GetAssetBalance(InMemoryVtxoStorage vtxoStorage, string assetId)
    {
        var vtxos = await vtxoStorage.GetVtxos(includeSpent: false);
        return vtxos
            .Where(v => v.Assets is { Count: > 0 })
            .SelectMany(v => v.Assets!)
            .Where(a => a.AssetId == assetId)
            .Aggregate(0UL, (sum, a) => sum + a.Amount);
    }
}
