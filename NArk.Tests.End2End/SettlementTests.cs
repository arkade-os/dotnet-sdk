using Microsoft.Extensions.Options;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Settlement;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Core.CoinSelector;
using NArk.Core.Services;
using NArk.Core.Settlement;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Tests.Common;
using NArk.Tests.End2End.Common;

namespace NArk.Tests.End2End.Core;

/// <summary>
/// Settlement against a live arkd. Unit tests can prove the engine plans and routes; only a
/// real server proves the transactions it builds are accepted — above all the asset packet,
/// whose inputs and outputs have to balance or the remainder is destroyed.
/// </summary>
[TestFixture]
public class SettlementTests
{
    private static readonly TimeSpan SettlementTimeout = TimeSpan.FromSeconds(60);

    [Test]
    public async Task SettlesBtcToAnotherWallet_OnceTheThresholdIsReached()
    {
        var alice = await FundedWalletHelper.GetFundedWallet();
        var bob = await FundedWalletHelper.GetFundedWallet();

        var bobAddress = (await bob.contractService.DeriveContract(
            bob.walletIdentifier, NextContractPurpose.Receive)).GetArkAddress();

        // Bob arrives with funds of his own, so the settlement is only visible as a delta.
        var bobBefore = await ReadBtcBalance(bob);

        // Alice holds 500 000 sats; the rule fires well below that and caps the payout, so the
        // rest stays behind as change rather than emptying the wallet.
        var rule = new SettlementConfig(
            alice.walletIdentifier,
            SettlementDestination.Ark(bobAddress.ToString(false)),
            Threshold: 400_000,
            MaxAmount: 100_000);

        using var engine = CreateEngine(alice, rule);
        await engine.StartAsync(CancellationToken.None);
        engine.QueueWallet(alice.walletIdentifier);

        await WaitForBtcBalance(bob, bobBefore + 100_000);

        await engine.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task SettlesAnAssetToAnotherWallet_AndKeepsWhatTheCapLeftBehind()
    {
        var alice = await FundedWalletHelper.GetFundedWallet();
        var bob = await FundedWalletHelper.GetFundedWallet();

        var (assetManager, _, _) = AssetTestHelpers.CreateAssetServices(alice);

        var issuance = await assetManager.IssueAsync(alice.walletIdentifier, new IssuanceParams(Amount: 1000));
        await AssetTestHelpers.PollUntilAssetVtxo(alice, issuance.AssetId, TimeSpan.FromSeconds(30));
        await AssetTestHelpers.PollAllScripts(alice);

        var bobAddress = (await bob.contractService.DeriveContract(
            bob.walletIdentifier, NextContractPurpose.Receive)).GetArkAddress();

        // Threshold and cap are in the asset's own units, not satoshis: fire at 500 units, move
        // 400, leave 600 as asset change.
        var rule = new SettlementConfig(
            alice.walletIdentifier,
            SettlementDestination.ArkAsset(bobAddress.ToString(false), issuance.AssetId),
            Threshold: 500,
            SourceAsset: issuance.AssetId,
            MaxAmount: 400);

        using var engine = CreateEngine(alice, rule);
        await engine.StartAsync(CancellationToken.None);
        engine.QueueWallet(alice.walletIdentifier);

        await AssetTestHelpers.PollUntilAssetBalance(bob, issuance.AssetId, 400, SettlementTimeout);

        // The half that matters: a partial asset settlement must return the remainder to the
        // settling wallet. Getting the packet wrong burns it instead, and only the server can
        // tell us which happened.
        await AssetTestHelpers.PollUntilAssetBalance(alice, issuance.AssetId, 600, SettlementTimeout);

        await engine.StopAsync(CancellationToken.None);
    }

    private static SettlementService CreateEngine(
        (ISafetyService safetyService, InMemoryWalletProvider walletProvider, string walletIdentifier,
            IVtxoStorage vtxoStorage, ContractService contractService, IContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) wallet,
        SettlementConfig rule)
    {
        var (_, coinService, intentStorage) = AssetTestHelpers.CreateAssetServices(wallet);

        var spending = new SpendingService(
            wallet.vtxoStorage, wallet.contracts, wallet.walletProvider, coinService,
            wallet.contractService, wallet.clientTransport, new DefaultCoinSelector(),
            wallet.safetyService, intentStorage);

        var options = Options.Create(new SettlementOptions
        {
            Debounce = TimeSpan.Zero,
            // The test queues the wallet itself; a heartbeat would only add a second attempt
            // racing the first.
            HeartbeatInterval = TimeSpan.Zero
        });

        var configProvider = new SingleRuleConfigProvider(rule);

        return new SettlementService(
            [new BalanceThresholdSettlementPolicy(configProvider)],
            [],
            [],
            new CompositeSettlementService(
            [
                new DestinationSweepSettlementService(
                    spending, wallet.contractService, wallet.clientTransport, options),
                new ArkAssetSettlementService(spending, wallet.contractService, wallet.clientTransport)
            ]),
            configProvider,
            spending,
            intentStorage,
            wallet.vtxoStorage,
            wallet.contracts,
            new EsploraBlockchain(SharedArkInfrastructure.ChopsticksEndpoint),
            options,
            []);
    }

    private static async Task<long> ReadBtcBalance(
        (ISafetyService safetyService, InMemoryWalletProvider walletProvider, string walletIdentifier,
            IVtxoStorage vtxoStorage, ContractService contractService, IContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) wallet)
    {
        var vtxos = await wallet.vtxoStorage.GetVtxos(includeSpent: false);
        return vtxos
            .Where(v => v.Assets is null or { Count: 0 })
            .Aggregate(0L, (sum, v) => sum + (long)v.Amount);
    }

    private static async Task WaitForBtcBalance(
        (ISafetyService safetyService, InMemoryWalletProvider walletProvider, string walletIdentifier,
            IVtxoStorage vtxoStorage, ContractService contractService, IContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) wallet,
        long expectedSats)
    {
        var deadline = DateTime.UtcNow + SettlementTimeout;
        long balance = 0;

        while (DateTime.UtcNow < deadline)
        {
            await AssetTestHelpers.PollAllScripts(wallet);

            balance = await ReadBtcBalance(wallet);
            if (balance >= expectedSats)
                return;

            await Task.Delay(1000);
        }

        Assert.Fail($"Timed out waiting for a settlement of {expectedSats} sats; balance is {balance}.");
    }

    /// <summary>Serves one rule, the way an application's own settings store would.</summary>
    private sealed class SingleRuleConfigProvider(SettlementConfig rule) : ISettlementConfigProvider
    {
        public Task<IReadOnlyCollection<SettlementConfig>> GetConfigs(
            string? walletId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<SettlementConfig>>(
                walletId is null || walletId == rule.WalletId ? [rule] : []);
    }
}
