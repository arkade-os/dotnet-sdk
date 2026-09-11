using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Services;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Tests.Arkade;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents;

public class NonInteractiveClientTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void CooperativeReceive_DoesNotSilentlySwitchToSignerless(bool onchain)
    {
        using var ctx = new Harness(onchain);
        var error = Assert.ThrowsAsync<InvalidOperationException>(() => onchain
            ? ctx.Service.ClaimOnchainReceiveAsync(ctx.Intent.Id) : ctx.Service.ClaimLightningReceiveAsync(ctx.Intent.Id));
        Assert.That(error!.Message, Does.Contain("no signer"));
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BackgroundAdvance_OrdinaryReceiveRemainsCooperative(bool onchain)
    {
        using var ctx = new Harness(onchain);
        ctx.Intent.Status = ArkadeSwapIntentStatus.Claimable;

        var result = await ctx.Service.AdvanceAsync(ctx.Intent.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(ArkadeIntentAction.ClaimReceive));
            Assert.That(result.Acted, Is.False);
            Assert.That(result.Error, Does.Contain("no signer"));
            Assert.That(ctx.Emulator.ArkTx, Is.Null);
        });
    }

    [Test]
    public async Task BackgroundAdvance_LinkedOnchainRefundIsLeftToRouteOwner()
    {
        using var ctx = new Harness(onchain: true);
        ctx.Intent.Metadata[ArkadeSwapMetadataKeys.ComposedOutgoingSwapId] = "outgoing-swap";

        var direct = await ctx.Service.AdvanceAsync(ctx.Intent.Id);
        var sweep = await ctx.Service.AdvanceAllAsync();

        Assert.Multiple(() =>
        {
            Assert.That(direct.Action, Is.EqualTo(ArkadeIntentAction.None));
            Assert.That(direct.Acted, Is.False);
            Assert.That(direct.Error, Is.Null);
            Assert.That(sweep, Is.Empty);
            Assert.That(ctx.Intent.Status, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
        });
    }

    [Test]
    public void CooperativeRefund_DoesNotSilentlySwitchToSignerless()
    {
        using var ctx = new Harness(false, outgoing: true);
        var error = Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Service.RefundLightningSendAsync(ctx.Intent.Id));
        Assert.That(error!.Message, Does.Contain("no signer"));
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Receive_ClaimsSplitFundingWithoutWalletSigner(bool onchain)
    {
        using var ctx = new Harness(onchain);
        var result = await ctx.Claim();
        Assert.That(result.Status, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(result.SpentTxid, Is.EqualTo(ctx.Emulator.ArkTx!.GetGlobalTransaction().GetHash().ToString()));
        Assert.That(ctx.Emulator.ArkTx.Outputs.Take(2).Select(o => o.Value.Satoshi), Is.EqualTo(new long[] { 30_000, 70_000 }));
        Assert.That(ctx.Emulator.ArkTx.Outputs.Take(2).All(o => o.ScriptPubKey.ToBytes()
            .SequenceEqual(ctx.Contract.NonInteractiveClaim!.ReceiverPkScript)), Is.True);
        await ctx.Wallet.DidNotReceive().GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Underfunding_DoesNotRevealPreimage(bool onchain)
    {
        using var ctx = new Harness(onchain, amounts: [30_000, 69_999]);
        var error = Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Claim());
        Assert.That(error!.Message, Does.Contain("less than"));
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
        Assert.That(ctx.Intent.Status, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingSecret_WatchOnlyFailsBeforeSubmission(bool onchain)
    {
        using var ctx = new Harness(onchain);
        ctx.Intent.Metadata.Remove(ArkadeSwapMetadataKeys.Preimage);
        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Claim());
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
    }

    [TestCase("asset")]
    [TestCase("subdust")]
    [TestCase("duplicate")]
    public void UnsafeSplitFunding_IsRejectedBeforePreimageSubmission(string kind)
    {
        using var ctx = new Harness(false, amounts: kind == "subdust" ? [100, 99_900] : null);
        if (kind == "asset") ctx.SetVtxos([ctx.Vtxos[0] with { Assets = [new VtxoAsset(new string('a', 68), 1)] }, ctx.Vtxos[1]]);
        if (kind == "duplicate") ctx.SetVtxos([ctx.Vtxos[1], ctx.Vtxos[1]]);
        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Claim());
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
    }

    [Test]
    public async Task MatureNinthLeaf_RefundsWithoutSignerOrPreimageToPinnedScript()
    {
        using var ctx = new Harness(false, outgoing: true);
        ctx.Intent.Metadata.Remove(ArkadeSwapMetadataKeys.Preimage);
        var refunded = await ctx.Service.RefundNonInteractiveAsync(ctx.Intent.Id);
        Assert.That(refunded.Status, Is.EqualTo(ArkadeSwapIntentStatus.Cancelled));
        Assert.That(ctx.Emulator.ArkTx!.GetGlobalTransaction().LockTime, Is.EqualTo(ctx.Contract.RefundLocktime));
        Assert.That(ctx.Emulator.ArkTx.Outputs.Take(2).All(o => o.ScriptPubKey.ToBytes()
            .SequenceEqual(ctx.Contract.NonInteractiveRefund!.SenderPkScript)), Is.True);
        Assert.That(ctx.Emulator.ArkTx.Outputs.Take(2).Select(o => o.Value.Satoshi), Is.EqualTo(new long[] { 30_000, 70_000 }));
        Assert.That(ctx.Emulator.ArkTx.GetGlobalTransaction().Inputs.All(i => i.Sequence.Value == 0xfffffffe), Is.True);
        await ctx.Wallet.DidNotReceive().GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Refund_RequiresMaturityAndNinthLeaf(bool immature)
    {
        using var ctx = new Harness(false, outgoing: true, ninthLeaf: immature);
        if (immature) ctx.Blockchain.GetChainTime(default).ReturnsForAnyArgs(new TimeHeight(DateTimeOffset.FromUnixTimeSeconds(1_799_999_999), 200));
        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Lightning.RefundNonInteractiveAsync(ctx.Intent.Id));
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
        Assert.That(ctx.Intent.Status, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
    }

    private sealed class Harness : IDisposable
    {
        internal readonly VHTLCv2Contract Contract;
        internal readonly ArkadeSwapIntent Intent;
        internal readonly IWalletProvider Wallet = Substitute.For<IWalletProvider>();
        internal readonly IBitcoinBlockchain Blockchain = Substitute.For<IBitcoinBlockchain>();
        internal readonly IVtxoStorage VtxoStorage = Substitute.For<IVtxoStorage>();
        internal readonly RecordingEmulator Emulator = new();
        internal readonly ArkVtxo[] Vtxos;
        internal readonly LightningIntentsClient Lightning;
        internal readonly ArkadeIntentsService Service;
        private readonly OnchainIntentsClient _onchain;
        private readonly bool _isOnchain;

        internal Harness(bool onchain, long[]? amounts = null, bool outgoing = false, bool ninthLeaf = true)
        {
            _isOnchain = onchain;
            Contract = NonInteractiveTestData.Contract(ninthLeaf: ninthLeaf);
            var funding = NonInteractiveTestData.Funding(Contract, amounts ?? [30_000, 70_000]);
            Vtxos = NonInteractiveTestData.Vtxos(Contract, funding);
            SetVtxos(Vtxos);
            Wallet.GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((IArkadeWalletSigner?)null);
            var transport = Substitute.For<IClientTransport>();
            transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(NonInteractiveTestData.ServerInfo());
            var contracts = Substitute.For<IContractStorage>();
            contracts.GetContracts().ReturnsForAnyArgs([Contract.ToEntity("watch-only", activityState: ContractActivityState.Active)]);
            Intent = new ArkadeSwapIntent
            {
                Id = "swap-1", WalletId = "watch-only", Type = outgoing ? ArkadeSwapIntentType.BtcToLightning
                    : onchain ? ArkadeSwapIntentType.OnchainToBtc : ArkadeSwapIntentType.LightningToBtc,
                OfferAmount = Money.Satoshis(100_000), WantAmount = Money.Satoshis(100_000),
                Status = ArkadeSwapIntentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
                SwapPkScript = Contract.GetScriptPubKey().ToHex(), SwapAddress = Contract.GetArkAddress().ToString(false),
                RefundLocktime = Contract.RefundLocktime.Value,
                Metadata = new Dictionary<string, string> { [ArkadeSwapMetadataKeys.Preimage] = Convert.ToHexString(NonInteractiveTestData.Preimage) }
            };
            var intents = Substitute.For<IArkadeIntentStorage>();
            intents.GetArkadeSwapIntents().ReturnsForAnyArgs([Intent]);
            Blockchain.GetChainTime(default).ReturnsForAnyArgs(new TimeHeight(DateTimeOffset.FromUnixTimeSeconds(1_800_000_001), 200));
            var spending = NonInteractiveTestData.Spending(Wallet, funding, Emulator);
            var clock = new FixedClock();
            Lightning = new LightningIntentsClient(transport, Substitute.For<IContractService>(), spending, intents,
                contracts, VtxoStorage, Wallet, blockchain: Blockchain, time: clock);
            _onchain = new OnchainIntentsClient(transport, Substitute.For<IContractService>(), spending, intents,
                contracts, VtxoStorage, Wallet, Blockchain, time: clock);
            Service = new ArkadeIntentsService(null!, Lightning, intents, VtxoStorage, transport, _onchain, time: clock);
        }

        internal void SetVtxos(ArkVtxo[] vtxos) => VtxoStorage.GetVtxos().ReturnsForAnyArgs(vtxos);
        internal Task<ArkadeSwapIntent> Claim() => _isOnchain
            ? Service.ClaimOnchainReceiveNonInteractiveAsync(Intent.Id) : Service.ClaimLightningReceiveNonInteractiveAsync(Intent.Id);
        public void Dispose() => Emulator.Dispose();
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1_799_990_000);
    }
}
