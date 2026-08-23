using BTCPayServer.Lightning;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Blockchain;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NArk.Swaps.Transformers;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.Core;
using NArk.Tests.End2End.TestPersistance;
using NArk.Core.Transformers;
using NArk.Tests.Common;
using NBitcoin;

using CoinSelector = NArk.Core.CoinSelector.DefaultCoinSelector;

namespace NArk.Tests.End2End.Swaps;

/// <summary>
/// Integration counterpart to <c>NArk.Tests.ReverseSwapAmountTests</c>:
/// verifies that <see cref="ReverseSwapFeePayer.Recipient"/> and
/// <see cref="ReverseSwapFeePayer.Sender"/> produce the correct amount
/// semantics on the real Boltz wire — invoice pinning, onchain pinning,
/// and the stored <see cref="ArkSwap.ExpectedAmount"/> in both modes.
/// </summary>
public class ReverseSwapAmountTests
{
    /// <summary>
    /// <see cref="ReverseSwapFeePayer.Recipient"/> mode: Boltz sets
    /// <c>invoiceAmount = requested</c> so payer wallets that verify the
    /// invoice (LNURL/LUD-06) accept it. The receiver nets
    /// <c>requested − fee</c> onchain, reflected in
    /// <see cref="ArkSwap.ExpectedAmount"/>.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task ReverseSwap_RecipientFeePayer_InvoiceEqualsRequested_OnchainIsLess(CancellationToken token)
    {
        const long requestedSats = 50_000;

        var prereq = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        await using var swapMgr = BuildSwapManager(prereq, swapStorage);
        await swapMgr.StartAsync(token);

        var invoice = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateReverseSwap(
                prereq.walletIdentifier,
                new CreateInvoiceParams(LightMoney.Satoshis(requestedSats), "Amount test Recipient", TimeSpan.FromHours(1)),
                ReverseSwapFeePayer.Recipient,
                token));

        var bolt11 = BOLT11PaymentRequest.Parse(invoice, Network.RegTest);
        var invoiceSats = (long)bolt11.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);

        Assert.That(invoiceSats, Is.EqualTo(requestedSats),
            "Recipient mode: invoice must equal the requested amount (LUD-06 compliant)");

        var swaps = await swapStorage.GetSwaps(walletIds: [prereq.walletIdentifier]);
        var swap = swaps.Single();
        Assert.That(swap.ExpectedAmount, Is.LessThan(requestedSats),
            "Recipient mode: expected onchain amount must be less than requested (Boltz fee deducted)");
        Assert.That(swap.ExpectedAmount, Is.GreaterThan(0),
            "Recipient mode: expected onchain amount must be positive");
    }

    /// <summary>
    /// <see cref="ReverseSwapFeePayer.Sender"/> mode: Boltz sets
    /// <c>onchainAmount = requested</c> so the receiver gets the exact
    /// amount, stored as <see cref="ArkSwap.ExpectedAmount"/>. The
    /// invoice is inflated by the fee — i.e. the payer absorbs it.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task ReverseSwap_SenderFeePayer_OnchainEqualsRequested_InvoiceIsInflated(CancellationToken token)
    {
        const long requestedSats = 50_000;

        var prereq = await FundedWalletHelper.GetFundedWallet();
        var swapStorage = TestStorage.CreateSwapStorage();
        await using var swapMgr = BuildSwapManager(prereq, swapStorage);
        await swapMgr.StartAsync(token);

        var invoice = await FulmineLiquidityHelper.RetryWithSettle(() =>
            swapMgr.InitiateReverseSwap(
                prereq.walletIdentifier,
                new CreateInvoiceParams(LightMoney.Satoshis(requestedSats), "Amount test Sender", TimeSpan.FromHours(1)),
                ReverseSwapFeePayer.Sender,
                token));

        var bolt11 = BOLT11PaymentRequest.Parse(invoice, Network.RegTest);
        var invoiceSats = (long)bolt11.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);

        Assert.That(invoiceSats, Is.GreaterThan(requestedSats),
            "Sender mode: invoice must be inflated above requested (fee absorbed by payer)");

        var swaps = await swapStorage.GetSwaps(walletIds: [prereq.walletIdentifier]);
        var swap = swaps.Single();
        Assert.That(swap.ExpectedAmount, Is.EqualTo(requestedSats),
            "Sender mode: expected onchain amount must equal the requested amount exactly");
    }

    private static SwapsManagementService BuildSwapManager(
        (ISafetyService safetyService, InMemoryWalletProvider walletProvider, string walletIdentifier,
            IVtxoStorage vtxoStorage, ContractService contractService, IContractStorage contracts,
            IClientTransport clientTransport, VtxoSynchronizationService vtxoSync) prereq,
        ISwapStorage swapStorage)
    {
        var chainTimeProvider = new NBXplorerBlockchain(Network.RegTest, SharedArkInfrastructure.NbxplorerEndpoint);
        var intentStorage = TestStorage.CreateIntentStorage();
        var coinService = new CoinService(prereq.clientTransport, prereq.contracts,
        [
            new PaymentContractTransformer(prereq.walletProvider),
            new HashLockedContractTransformer(prereq.walletProvider),
            new VHTLCContractTransformer(prereq.walletProvider, chainTimeProvider)
        ]);
        var spendingService = new SpendingService(prereq.vtxoStorage, prereq.contracts,
            prereq.walletProvider, coinService, prereq.contractService, prereq.clientTransport,
            new CoinSelector(), prereq.safetyService, intentStorage);
        var boltzOpts = new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions
        {
            BoltzUrl = SharedSwapInfrastructure.BoltzEndpoint.ToString(),
            WebsocketUrl = SharedSwapInfrastructure.BoltzWsEndpoint.ToString()
        });
        var boltzClient = new BoltzClient(new HttpClient(), boltzOpts);
        var cachedClient = new CachedBoltzClient(new HttpClient(), boltzOpts);
        var boltzProvider = new BoltzSwapProvider(boltzClient, new BoltzLimitsValidator(cachedClient),
            prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider, swapStorage,
            prereq.contractService, prereq.contracts, prereq.safetyService, intentStorage, chainTimeProvider);
        return new SwapsManagementService(
            new ISwapProvider[] { boltzProvider },
            spendingService, prereq.clientTransport, prereq.vtxoStorage, prereq.walletProvider,
            swapStorage, prereq.contractService, prereq.contracts, prereq.safetyService, intentStorage,
            chainTimeProvider);
    }
}
