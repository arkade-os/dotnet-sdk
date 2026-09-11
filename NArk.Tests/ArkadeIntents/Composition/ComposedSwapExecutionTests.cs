using System.Numerics;
using System.Security.Cryptography;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Services;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Tests.Arkade;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Composition;

/// <summary>Payment boundaries and recovery over real NI spending and EVM receipt verification.</summary>
public class ComposedSwapExecutionTests
{
    /// <summary>Both sides of the exact funding boundary must preserve the secret.</summary>
    [TestCase(false, -1)]
    [TestCase(false, 1)]
    [TestCase(true, -1)]
    [TestCase(true, 1)]
    public void LinkedIngress_RequiresExactLiveFundingBeforePublishingSecret(bool onchain, int delta)
    {
        using var ctx = new Harness(onchain, delta);
        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.ClaimIngress());
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
        Assert.That(ctx.Outgoing.Status, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
        Assert.That(ctx.Ingress.Status, Is.EqualTo(ArkadeSwapIntentStatus.Claimable));
    }

    /// <summary>Automatic corridor advancement must honor the durable route binding.</summary>
    [TestCase(false, "hash")]
    [TestCase(true, "hash")]
    [TestCase(false, "amount")]
    [TestCase(true, "amount")]
    [TestCase(false, "wallet")]
    [TestCase(true, "wallet")]
    [TestCase(false, "payout")]
    [TestCase(true, "payout")]
    public void GenericClaim_CannotBypassPersistedRouteValidation(bool onchain, string mismatch)
    {
        using var ctx = new Harness(onchain);
        if (mismatch == "hash") ctx.Outgoing.PaymentHash = new string('0', 64);
        if (mismatch == "amount") ctx.Outgoing.OfferAmount = Money.Satoshis(99_999);
        if (mismatch == "wallet") ctx.Outgoing.WalletId = "another-wallet";
        if (mismatch == "payout") ctx.Ingress.Metadata["composedPayoutPkScript"] = "5120" + new string('a', 64);
        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.ClaimIngress());
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
    }

    /// <summary>Ingress fulfillment describes M-to-L delivery only.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ExactSplitIngress_FundsPinnedLWithoutSettlingOutgoing(bool onchain)
    {
        using var ctx = new Harness(onchain);
        var result = await ctx.ClaimIngress();
        Assert.That(result.Status, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(ctx.Outgoing.Status, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
        Assert.That(ctx.Emulator.ArkTx!.Outputs.Take(2).Select(o => o.Value.Satoshi),
            Is.EqualTo(new long[] { 30_000, 70_000 }));
        Assert.That(ctx.Emulator.ArkTx.Outputs.Take(2).All(o => o.ScriptPubKey == ctx.L.GetScriptPubKey()), Is.True);
        await ctx.Wallet.DidNotReceive().GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BackgroundAdvance_LeavesLinkedIngressToComposedExecutor(bool onchain)
    {
        using var ctx = new Harness(onchain);
        var service = new ArkadeIntentsService(
            null!, ctx.Lightning, ctx.Storage, ctx.VtxoStorage, ctx.Transport, ctx.Onchain,
            time: new FixedClock());

        var result = await service.AdvanceAsync(ctx.Ingress.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(ArkadeIntentAction.None));
            Assert.That(result.Acted, Is.False);
            Assert.That(result.Error, Is.Null);
            Assert.That(ctx.Ingress.Status, Is.EqualTo(ArkadeSwapIntentStatus.Claimable));
            Assert.That(ctx.Emulator.ArkTx, Is.Null);
        });
        Assert.That(await service.AdvanceAllAsync(), Is.Empty);
        var composed = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id, ctx.Ingress.Id);
        Assert.That(composed.IngressStatus, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(ctx.Emulator.ArkTx, Is.Not.Null);
        await ctx.Wallet.DidNotReceive().GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The outgoing EVM corridor retains its signerless Arkade refund.</summary>
    [Test]
    public async Task EvmOutgoing_RefundsThroughNinthLeafWithoutPreimage()
    {
        using var ctx = new Harness(false, refund: true);
        ctx.Outgoing.Metadata.Remove(ArkadeSwapMetadataKeys.Preimage);
        var result = await ctx.Lightning.RefundNonInteractiveAsync(ctx.Outgoing.Id);
        Assert.That(result.Status, Is.EqualTo(ArkadeSwapIntentStatus.Cancelled));
        Assert.That(ctx.Emulator.ArkTx!.Outputs.Take(2).All(o =>
            o.ScriptPubKey.ToBytes().SequenceEqual(ctx.L.NonInteractiveRefund!.SenderPkScript)), Is.True);
        await ctx.Wallet.DidNotReceive().GetSignerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Only a funded pending lock or an observer-proven claim state permits EVM work.</summary>
    [TestCase(ArkadeSwapIntentStatus.Funding)]
    [TestCase(ArkadeSwapIntentStatus.Cancelled)]
    [TestCase(ArkadeSwapIntentStatus.Resolved)]
    [TestCase(ArkadeSwapIntentStatus.Recoverable)]
    public async Task EvmClaim_WaitsForProvenOutgoingClaimable(ArkadeSwapIntentStatus status)
    {
        using var ctx = new Harness(false);
        ctx.Outgoing.Status = status;
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);
        Assert.That(result.OutgoingStatus, Is.EqualTo(status));
        Assert.That(result.EvmClaimTxid, Is.Null);
        Assert.That(ctx.Sender.Calls, Is.Zero);
        Assert.That(ctx.Rpc.ReceivedCalls(), Is.Empty);
    }

    /// <summary>A funded L triggers the EVM claim before the solver can spend L with the revealed preimage.</summary>
    [Test]
    public async Task EvmClaim_FundedPendingOutgoingBreaksThePreimageHandshake()
    {
        using var ctx = new Harness(false) { OutgoingFunded = true };
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);
        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(result.EvmClaimTxid, Is.EqualTo(Harness.ClaimTxid));
        Assert.That(ctx.Sender.Calls, Is.EqualTo(1));
    }

    /// <summary>An unfunded L remains pending without touching the EVM chain.</summary>
    [Test]
    public async Task EvmClaim_UnfundedPendingOutgoingIsANoOp()
    {
        using var ctx = new Harness(false);
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);
        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
        Assert.That(ctx.Sender.Calls, Is.Zero);
        Assert.That(ctx.Rpc.ReceivedCalls(), Is.Empty);
    }

    /// <summary>Both sides of the outgoing exact-funding boundary refuse to reveal the secret.</summary>
    [TestCase(-1)]
    [TestCase(1)]
    public void EvmClaim_PendingOutgoingRequiresExactFunding(int delta)
    {
        using var ctx = new Harness(false);
        ctx.SetOutgoingFunding(delta);
        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));
        Assert.That(ctx.Sender.Calls, Is.Zero);
        Assert.That(ctx.Rpc.ReceivedCalls(), Is.Empty);
    }

    /// <summary>An Arkade asset on L cannot masquerade as the quoted BTC funding.</summary>
    [Test]
    public void EvmClaim_PendingOutgoingRejectsAssetFunding()
    {
        using var ctx = new Harness(false);
        ctx.SetOutgoingFunding(asset: true);
        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));
        Assert.That(ctx.Sender.Calls, Is.Zero);
        Assert.That(ctx.Rpc.ReceivedCalls(), Is.Empty);
    }

    [TestCase("unrolled")]
    [TestCase("unconfirmed")]
    [TestCase("expired")]
    public void EvmClaim_PendingOutgoingRejectsUnspendableFunding(string state)
    {
        using var ctx = new Harness(false);
        ctx.SetUnspendableOutgoingFunding(state);

        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));
        Assert.That(ctx.Sender.Calls, Is.Zero);
    }

    [Test]
    public void EvmClaim_RejectsWhenArkadeRefundMarginWasConsumed()
    {
        using var ctx = new Harness(false) { OutgoingFunded = true };
        ctx.Outgoing.RefundLocktime = 1_799_997_299;

        Assert.ThrowsAsync<EvmSwapProofException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));
        Assert.That(ctx.Sender.Calls, Is.Zero);
    }

    /// <summary>Source delivery advances ingress without prematurely claiming ERC20.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Execution_ClaimsMToLButDoesNotClaimEvmOrSettle(bool onchain)
    {
        using var ctx = new Harness(onchain);
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id, ctx.Ingress.Id);
        Assert.That(result.IngressStatus, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Pending));
        Assert.That(result.EvmClaimTxid, Is.Null);
        Assert.That(ctx.Sender.Calls, Is.Zero);
        Assert.That(ctx.Emulator.ArkTx, Is.Not.Null);
    }

    /// <summary>Only verified delivery is durable completion and subsequent execution is a no-op.</summary>
    [Test]
    public async Task VerifiedEvmClaim_PersistsSubmittedIdentityBeforeBroadcastThenFinalProof()
    {
        using var ctx = new Harness(false);
        ctx.Outgoing.Status = ArkadeSwapIntentStatus.Claimable;
        ctx.Outgoing.SpentTxid = new string('c', 64);
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);
        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(result.EvmClaimTxid, Is.EqualTo(Harness.ClaimTxid));
        Assert.That(result.LockProof, Is.EqualTo(new EvmLockProof(100, 100, 1_799_980_000)));
        Assert.That(result.DeliveredAmount, Is.EqualTo("1000000"));
        Assert.That(ctx.Sender.Calls, Is.EqualTo(1));
        Assert.That(ctx.Sender.Request!.Data, Is.EqualTo(Erc20SwapCodec.ClaimForCall(NonInteractiveTestData.Preimage, ctx.Values)));
        Assert.That(ctx.SavedStates, Is.EqualTo(new[] { (ArkadeSwapIntentStatus.Claimable, (string?)null, (string?)null),
            (ArkadeSwapIntentStatus.Claimable, (string?)Harness.ClaimTxid, (string?)null),
            (ArkadeSwapIntentStatus.Fulfilled, Harness.ClaimTxid, (string?)Harness.ClaimTxid) }));
        Assert.That(result.ToString(), Does.Not.Contain(Convert.ToHexString(NonInteractiveTestData.Preimage)));
        var repeated = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);
        Assert.That(repeated, Is.EqualTo(result));
        Assert.That(ctx.Sender.Calls, Is.EqualTo(1));
    }

    /// <summary>A legacy hash-only journal resumes by receipt lookup without signing another transaction.</summary>
    [Test]
    public async Task Restart_VerifiesPreparedTransactionWithoutAnotherSubmission()
    {
        using var ctx = new Harness(false);
        ctx.Outgoing.Status = ArkadeSwapIntentStatus.Claimable;
        ctx.Outgoing.SpentTxid = new string('c', 64);
        ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid] = Harness.ClaimTxid;
        ctx.LockIsPresent = false;
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);
        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(ctx.Sender.Calls, Is.Zero);
    }

    /// <summary>A prepared pending claim remains recoverable after the solver consumes L.</summary>
    [Test]
    public async Task Restart_VerifiesPreparedPendingTransactionAfterFundingIsSpent()
    {
        using var ctx = new Harness(false);
        ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid] = Harness.ClaimTxid;
        ctx.LockIsPresent = false;
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);
        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(ctx.Sender.Calls, Is.Zero);
    }

    [Test]
    public async Task PreparedClaim_MinedAfterDeadlineIsReconciledBeforeArkadeRefundWithoutBroadcast()
    {
        using var ctx = new Harness(false) { OutgoingFunded = true, LockIsPresent = false };
        ctx.Outgoing.Status = ArkadeSwapIntentStatus.Refundable;
        ctx.Outgoing.RefundLocktime = 1_799_989_999;
        ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid] = Harness.ClaimTxid;
        ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction] = "0x02aa";

        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);

        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(result.EvmClaimTxid, Is.EqualTo(Harness.ClaimTxid));
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
        Assert.That(ctx.Sender.Broadcasts, Is.Zero);
    }

    [Test]
    public void PreparedClaim_UnminedAfterDeadlineIsNeitherRebroadcastNorRefunded()
    {
        using var ctx = new Harness(false) { OutgoingFunded = true, RequireBroadcastForReceipt = true };
        ctx.Outgoing.Status = ArkadeSwapIntentStatus.Refundable;
        ctx.Outgoing.RefundLocktime = 1_799_989_999;
        ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid] = Harness.ClaimTxid;
        ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction] = "0x02aa";

        Assert.ThrowsAsync<EvmSwapProofException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Outgoing.Status, Is.EqualTo(ArkadeSwapIntentStatus.Refundable));
            Assert.That(ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid],
                Is.EqualTo(Harness.ClaimTxid));
            Assert.That(ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction],
                Is.EqualTo("0x02aa"));
            Assert.That(ctx.Sender.Broadcasts, Is.Zero);
            Assert.That(ctx.Emulator.ArkTx, Is.Null);
        });
    }

    [Test]
    public async Task Restart_RebroadcastsTheExactPreparedTransaction()
    {
        using var ctx = new Harness(false) { OutgoingFunded = true, RequireBroadcastForReceipt = true };
        ctx.Sender.FailAfterPrepareOnce = true;

        Assert.ThrowsAsync<IOException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);

        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(ctx.Sender.Broadcasts, Is.EqualTo(1));
        Assert.That(ctx.Sender.ResumedPrepared, Is.EqualTo(new EvmPreparedTransaction(Harness.ClaimTxid, "0x02aa")));
    }

    /// <summary>A receipt found on restart is verified without needlessly replaying prepared bytes.</summary>
    [Test]
    public async Task UncertainReceipt_RemainsClaimableAndRestartVerifiesBeforeRebroadcast()
    {
        using var ctx = new Harness(false);
        ctx.Outgoing.Status = ArkadeSwapIntentStatus.Claimable;
        ctx.Outgoing.SpentTxid = new string('c', 64);
        ctx.FailReceipt = true;
        Assert.ThrowsAsync<TimeoutException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));
        Assert.That(ctx.Outgoing.Status, Is.EqualTo(ArkadeSwapIntentStatus.Claimable));
        Assert.That(ctx.Outgoing.Metadata.ContainsKey(ArkadeSwapMetadataKeys.EvmClaimTxid), Is.False);
        Assert.That(ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid], Is.EqualTo(Harness.ClaimTxid));
        ctx.FailReceipt = false;
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);
        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.Multiple(() =>
        {
            Assert.That(ctx.Sender.Calls, Is.EqualTo(1));
            Assert.That(ctx.Sender.Broadcasts, Is.EqualTo(1));
        });
    }

    /// <summary>Composition rejects inconsistent persisted legs before revealing their secret.</summary>
    [TestCase("hash")]
    [TestCase("amount")]
    [TestCase("payout")]
    [TestCase("wallet")]
    [TestCase("secret")]
    public void Execution_RejectsMismatchedRouteBeforeAnySecretSubmission(string mismatch)
    {
        using var ctx = new Harness(false);
        if (mismatch == "hash") ctx.Ingress.PaymentHash = new string('0', 64);
        if (mismatch == "amount") ctx.Ingress.WantAmount = Money.Satoshis(100_001);
        if (mismatch == "wallet") ctx.Ingress.WalletId = "other";
        if (mismatch == "payout") ctx.Ingress.Metadata[ArkadeSwapMetadataKeys.ComposedPayoutPkScript] = "5120" + new string('a', 64);
        if (mismatch == "secret") ctx.Ingress.Metadata[ArkadeSwapMetadataKeys.Preimage] = new string('0', 64);
        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id, ctx.Ingress.Id));
        Assert.That(ctx.Sender.Calls, Is.Zero);
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
    }

    /// <summary>EVM execution is bound to both policy and proven Arkade spend.</summary>
    [TestCase("chain")]
    [TestCase("contract")]
    [TestCase("spend")]
    [TestCase("secret")]
    public void Execution_RejectsInvalidOutgoingEvidenceBeforeClaimFor(string mismatch)
    {
        using var ctx = new Harness(false);
        ctx.Outgoing.Status = ArkadeSwapIntentStatus.Claimable;
        ctx.Outgoing.SpentTxid = new string('c', 64);
        if (mismatch == "chain") ctx.Outgoing.ToAssetId = "eip155:1/erc20:" + ctx.Values.TokenAddress;
        if (mismatch == "contract") ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.EvmSwapContractAddress] = "0x" + new string('5', 40);
        if (mismatch == "spend") ctx.Outgoing.SpentTxid = null;
        if (mismatch == "secret") ctx.Outgoing.Metadata[ArkadeSwapMetadataKeys.Preimage] = new string('0', 64);
        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));
        Assert.That(ctx.Sender.Calls, Is.Zero);
    }

    /// <summary>Refundable routes do not depend on the route preimage or EVM availability.</summary>
    [Test]
    public async Task Execution_RefundableDirectRouteUsesNiAndNeverClaimsEvm()
    {
        using var ctx = new Harness(false, refund: true);
        ctx.Outgoing.Metadata.Remove(ArkadeSwapMetadataKeys.Preimage);
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);
        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Cancelled));
        Assert.That(result.EvmClaimTxid, Is.Null);
        Assert.That(ctx.Sender.Calls, Is.Zero);
        Assert.That(ctx.Emulator.ArkTx, Is.Not.Null);
    }

    /// <summary>Do not reveal P if L cannot retain its promised independent refund path.</summary>
    [TestCase(false, "deadline")]
    [TestCase(true, "deadline")]
    [TestCase(false, "ninth-leaf")]
    [TestCase(true, "ninth-leaf")]
    public void Ingress_RequiresLiveOutgoingDeadlineAndNiRefund(bool onchain, string fault)
    {
        using var ctx = new Harness(onchain, ninthLeaf: fault != "ninth-leaf");
        if (fault == "deadline") ctx.Outgoing.RefundLocktime = 1_799_980_000;
        Assert.ThrowsAsync<InvalidOperationException>(() => ctx.ClaimIngress());
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
    }

    /// <summary>Persisted ingress fulfillment is a repeatable no-op even after its deadline.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task IngressAlreadyFulfilled_DoesNotRevealOrSpendAgain(bool onchain)
    {
        using var ctx = new Harness(onchain);
        ctx.Ingress.Status = ArkadeSwapIntentStatus.Fulfilled;
        ctx.Ingress.RefundLocktime = 1;
        var result = await ctx.ClaimIngress();
        Assert.That(result, Is.SameAs(ctx.Ingress));
        Assert.That(ctx.Emulator.ArkTx, Is.Null);
    }

    /// <summary>A failed receipt cannot become settlement even when its transaction id was saved.</summary>
    [Test]
    public void FailedReceipt_DoesNotPersistFulfilledOrClaimProof()
    {
        using var ctx = new Harness(false);
        ctx.Outgoing.Status = ArkadeSwapIntentStatus.Claimable;
        ctx.Outgoing.SpentTxid = new string('c', 64);
        ctx.ReceiptSucceeded = false;
        Assert.ThrowsAsync<EvmSwapProofException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));
        Assert.That(ctx.Outgoing.Status, Is.EqualTo(ArkadeSwapIntentStatus.Claimable));
        Assert.That(ctx.Outgoing.Metadata.ContainsKey(ArkadeSwapMetadataKeys.EvmClaimTxid), Is.False);
        Assert.That(ctx.SavedStates.All(s => s.Item1 == ArkadeSwapIntentStatus.Claimable), Is.True);
    }

    /// <summary>Failure to journal before broadcast stops the sender before submission.</summary>
    [Test]
    public void PreparedPersistenceFailure_PreventsBroadcast()
    {
        using var ctx = new Harness(false);
        ctx.Outgoing.Status = ArkadeSwapIntentStatus.Claimable;
        ctx.Outgoing.SpentTxid = new string('c', 64);
        ctx.Storage.WhenForAnyArgs(s => s.SaveArkadeSwapIntent(default!, default)).Do(call =>
        {
            if (call.ArgAt<ArkadeSwapIntent>(0).Metadata.ContainsKey(ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid))
                throw new IOException();
        });
        Assert.ThrowsAsync<IOException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));
        Assert.That(ctx.Sender.Broadcasts, Is.Zero);
        Assert.That(ctx.Outgoing.Metadata.ContainsKey(ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid), Is.False);
        Assert.That(ctx.Outgoing.Status, Is.EqualTo(ArkadeSwapIntentStatus.Claimable));
    }

    /// <summary>A failed final write verifies the already-mined prepared transaction without replay.</summary>
    [Test]
    public async Task CompletionPersistenceFailure_RetryVerifiesPreparedTransactionWithoutRebroadcast()
    {
        using var ctx = new Harness(false);
        ctx.Outgoing.Status = ArkadeSwapIntentStatus.Claimable;
        ctx.Outgoing.SpentTxid = new string('c', 64);
        var fail = true;
        ctx.Storage.WhenForAnyArgs(s => s.SaveArkadeSwapIntent(default!, default)).Do(call =>
        {
            if (fail && call.ArgAt<ArkadeSwapIntent>(0).Status == ArkadeSwapIntentStatus.Fulfilled)
                throw new IOException();
        });
        Assert.ThrowsAsync<IOException>(() => ctx.Execution().AdvanceAsync(ctx.Outgoing.Id));
        Assert.That(ctx.Outgoing.Status, Is.EqualTo(ArkadeSwapIntentStatus.Claimable));
        Assert.That(ctx.Outgoing.Metadata.ContainsKey(ArkadeSwapMetadataKeys.EvmClaimTxid), Is.False);
        Assert.That(ctx.Outgoing.Metadata.ContainsKey(ArkadeSwapMetadataKeys.EvmDeliveredAmount), Is.False);
        fail = false;
        var result = await ctx.Execution().AdvanceAsync(ctx.Outgoing.Id);
        Assert.That(result.OutgoingStatus, Is.EqualTo(ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(result.LockProof, Is.EqualTo(new EvmLockProof(100, 100, 1_799_980_000)));
        Assert.That(result.DeliveredAmount, Is.EqualTo("1000000"));
        Assert.That(ctx.Sender.Broadcasts, Is.EqualTo(1));
    }

    /// <summary>Concurrent callers on one executor share durable state and submit at most once.</summary>
    [Test]
    public async Task ConcurrentAdvance_OnlySubmitsOneEvmClaim()
    {
        using var ctx = new Harness(false);
        ctx.Outgoing.Status = ArkadeSwapIntentStatus.Claimable;
        ctx.Outgoing.SpentTxid = new string('c', 64);
        var execution = ctx.Execution();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => execution.AdvanceAsync(ctx.Outgoing.Id)));
        Assert.That(results.Distinct().Count(), Is.EqualTo(1));
        Assert.That(ctx.Sender.Broadcasts, Is.EqualTo(1));
    }

    private sealed class Harness : IDisposable
    {
        internal readonly VHTLCv2Contract L;
        internal readonly VHTLCv2Contract M;
        internal readonly ArkadeSwapIntent Outgoing;
        internal readonly ArkadeSwapIntent Ingress;
        internal readonly IWalletProvider Wallet = Substitute.For<IWalletProvider>();
        internal readonly IArkadeIntentStorage Storage = Substitute.For<IArkadeIntentStorage>();
        internal readonly IVtxoStorage VtxoStorage = Substitute.For<IVtxoStorage>();
        internal readonly IContractStorage Contracts = Substitute.For<IContractStorage>();
        internal readonly IClientTransport Transport = Substitute.For<IClientTransport>();
        internal readonly IBitcoinBlockchain Blockchain = Substitute.For<IBitcoinBlockchain>();
        internal readonly RecordingEmulator Emulator = new();
        internal readonly LightningIntentsClient Lightning;
        internal readonly OnchainIntentsClient Onchain;
        internal const string ClaimTxid = "0xdddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        internal readonly IEvmSwapRpc Rpc = Substitute.For<IEvmSwapRpc>();
        internal readonly Sender Sender;
        internal readonly List<(ArkadeSwapIntentStatus, string?, string?)> SavedStates = [];
        internal bool FailReceipt;
        internal bool ReceiptSucceeded = true;
        internal bool LockIsPresent = true;
        internal bool OutgoingFunded;
        internal bool RequireBroadcastForReceipt;
        internal Erc20SwapValues Values => new(Outgoing.PaymentHash!, 1_000_000,
            "0x1111111111111111111111111111111111111111", "0x2222222222222222222222222222222222222222",
            "0x3333333333333333333333333333333333333333", 200);
        private readonly bool _onchain;

        internal Harness(bool onchain, int delta = 0, bool refund = false, bool ninthLeaf = true)
        {
            _onchain = onchain;
            L = NonInteractiveTestData.Contract(ninthLeaf: ninthLeaf);
            M = new VHTLCv2Contract(NonInteractiveTestData.Descriptor(2), NonInteractiveTestData.Descriptor(3),
                NonInteractiveTestData.Descriptor(4), L.Hash, L.RefundLocktime,
                new Sequence(144), new Sequence(288), new Sequence(432),
                new VHTLCv2NonInteractiveClaim(L.GetScriptPubKey().ToBytes(), NBitcoin.Secp256k1.ECXOnlyPubKey.Create(NonInteractiveTestData.KeyBytes(5))),
                L.NonInteractiveRefund);
            var contract = refund ? L : M;
            var funding = NonInteractiveTestData.Funding(contract, 30_000, 70_000 + delta);
            OutgoingFunded = refund;
            var ingressVtxos = NonInteractiveTestData.Vtxos(contract, funding);
            var outgoingVtxos = NonInteractiveTestData.Vtxos(L, NonInteractiveTestData.Funding(L, 30_000, 70_000));
            VtxoStorage.GetVtxos().ReturnsForAnyArgs(call =>
            {
                var scripts = call.ArgAt<IReadOnlyCollection<string>?>(0);
                return scripts?.Contains(L.GetScriptPubKey().ToHex(), StringComparer.OrdinalIgnoreCase) == true
                    ? OutgoingFunded ? outgoingVtxos : []
                    : ingressVtxos;
            });
            Contracts.GetContracts().ReturnsForAnyArgs(call =>
            {
                var scripts = call.ArgAt<string[]?>(1);
                return new[] { L, M }.Where(c => scripts is null || scripts.Contains(c.GetScriptPubKey().ToHex()))
                    .Select(c => c.ToEntity("watch-only", activityState: ContractActivityState.Active)).ToArray();
            });
            Outgoing = Intent(new string('a', 64), L, ArkadeSwapIntentType.BtcToEvm,
                refund ? ArkadeSwapIntentStatus.Refundable : ArkadeSwapIntentStatus.Pending);
            Outgoing.ToAssetId = "eip155:31337/erc20:0x1111111111111111111111111111111111111111";
            Outgoing.WithEvmMetadata(new EvmSwapMetadata(Convert.ToHexString(NonInteractiveTestData.Preimage).ToLowerInvariant(),
                "1000000", "0x1111111111111111111111111111111111111111", "0x2222222222222222222222222222222222222222",
                "0x3333333333333333333333333333333333333333", "200", "0x4444444444444444444444444444444444444444"));
            Ingress = Intent(new string('b', 64), M, onchain ? ArkadeSwapIntentType.OnchainToBtc
                : ArkadeSwapIntentType.LightningToBtc, ArkadeSwapIntentStatus.Claimable);
            Ingress.Metadata["composedOutgoingSwapId"] = Outgoing.Id;
            Ingress.Metadata["composedPayoutPkScript"] = Outgoing.SwapPkScript;
            Storage.GetArkadeSwapIntents().ReturnsForAnyArgs(call => new[] { Outgoing, Ingress }
                .Where(i => call.ArgAt<string?>(0) is not { } id || id == i.Id).ToArray());
            Transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(NonInteractiveTestData.ServerInfo());
            Blockchain.GetChainTime(default).ReturnsForAnyArgs(
                new TimeHeight(DateTimeOffset.FromUnixTimeSeconds(1_800_000_001), 200));
            var spending = NonInteractiveTestData.Spending(Wallet, funding, Emulator);
            Lightning = new LightningIntentsClient(Transport, Substitute.For<IContractService>(), spending,
                Storage, Contracts, VtxoStorage, Wallet, blockchain: Blockchain, time: new FixedClock());
            Onchain = new OnchainIntentsClient(Transport, Substitute.For<IContractService>(), spending,
                Storage, Contracts, VtxoStorage, Wallet, Blockchain, time: new FixedClock());
            Sender = new Sender(this);
            Storage.WhenForAnyArgs(s => s.SaveArkadeSwapIntent(default!, default)).Do(call =>
            {
                var intent = call.ArgAt<ArkadeSwapIntent>(0);
                SavedStates.Add((intent.Status, intent.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimSubmittedTxid),
                    intent.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimTxid)));
            });
            Rpc.GetChainIdAsync(default).ReturnsForAnyArgs(new BigInteger(31_337));
            Rpc.GetBlockNumberAsync(default).ReturnsForAnyArgs(new BigInteger(100));
            Rpc.GetBlockTimestampAsync(default, default).ReturnsForAnyArgs(1_799_980_000L);
            Rpc.CallAsync(default!, default!, default, default).ReturnsForAnyArgs(_ =>
            {
                var result = new byte[32];
                if (LockIsPresent) result[31] = 1;
                return result;
            });
            Rpc.WaitForReceiptAsync(default!, default).ReturnsForAnyArgs(_ =>
            {
                if (FailReceipt) throw new TimeoutException();
                if (RequireBroadcastForReceipt && Sender.Broadcasts == 0) throw new TimeoutException();
                LockIsPresent = false;
                return new EvmTransactionReceipt(ClaimTxid, ReceiptSucceeded,
                [
                    new(Policy.SwapContractAddress, [Erc20SwapCodec.ClaimTopic, "0x" + Values.PaymentHash],
                        "0x" + Convert.ToHexString(NonInteractiveTestData.Preimage).ToLowerInvariant()),
                    new(Values.TokenAddress, [Erc20SwapCodec.TransferTopic, Topic(Policy.SwapContractAddress), Topic(Values.ClaimAddress)],
                        "0x" + Values.Amount.ToString("x64"))
                ]);
            });
        }

        internal EvmSendPolicy Policy => new()
        {
            ChainId = 31_337,
            TokenAddress = Values.TokenAddress,
            SwapContractAddress = "0x4444444444444444444444444444444444444444",
            FastestSecondsPerBlock = 1,
            SlowestSecondsPerBlock = 1,
            MinConfirmations = 1,
            MinAgeSeconds = 1,
            MinimumClaimWindowSeconds = 0
        };
        internal ComposedSwapExecutionClient Execution() => new(Storage, VtxoStorage, Blockchain, Contracts, Transport, Lightning, Onchain,
            Rpc, Sender, Policy, new FixedClock());
        private static string Topic(string address) => "0x" + new string('0', 24) + address[2..];

        internal Task<ArkadeSwapIntent> ClaimIngress() => _onchain
            ? Onchain.ClaimNonInteractiveAsync(Ingress.Id) : Lightning.ClaimNonInteractiveAsync(Ingress.Id);

        internal void SetOutgoingFunding(int delta = 0, bool asset = false)
        {
            var vtxos = NonInteractiveTestData.Vtxos(L,
                NonInteractiveTestData.Funding(L, 30_000, 70_000 + delta));
            if (asset)
                vtxos[0] = vtxos[0] with { Assets = [new VtxoAsset(new string('f', 68), 1)] };
            VtxoStorage.GetVtxos().ReturnsForAnyArgs(vtxos);
        }

        internal void SetUnspendableOutgoingFunding(string state)
        {
            var vtxos = NonInteractiveTestData.Vtxos(L,
                NonInteractiveTestData.Funding(L, 30_000, 70_000));
            vtxos[0] = state switch
            {
                "unrolled" => vtxos[0] with { Unrolled = true },
                "unconfirmed" => vtxos[0] with
                {
                    Metadata = new Dictionary<string, string> { [ArkVtxo.ConfirmedMetadataKey] = bool.FalseString }
                },
                "expired" => vtxos[0] with { ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000) },
                _ => throw new ArgumentOutOfRangeException(nameof(state))
            };
            VtxoStorage.GetVtxos().ReturnsForAnyArgs(vtxos);
        }

        private static ArkadeSwapIntent Intent(string id, VHTLCv2Contract contract,
            ArkadeSwapIntentType type, ArkadeSwapIntentStatus status) => new()
            {
                Id = id,
                WalletId = "watch-only",
                Type = type,
                Status = status,
                OfferAmount = Money.Satoshis(100_000),
                WantAmount = Money.Satoshis(100_000),
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_799_990_000),
                SwapPkScript = contract.GetScriptPubKey().ToHex(),
                SwapAddress = contract.GetArkAddress().ToString(false),
                RefundLocktime = contract.RefundLocktime.Value,
                PaymentHash = Convert.ToHexString(SHA256.HashData(NonInteractiveTestData.Preimage)).ToLowerInvariant(),
                Metadata = new() { [ArkadeSwapMetadataKeys.Preimage] = Convert.ToHexString(NonInteractiveTestData.Preimage) }
            };

        public void Dispose() => Emulator.Dispose();
    }

    private sealed class Sender(Harness harness) : IEvmDurableTransactionSender
    {
        internal int Calls;
        internal int Broadcasts;
        internal bool FailAfterPrepareOnce;
        internal EvmPreparedTransaction? ResumedPrepared;
        internal EvmTransactionRequest? Request;
        public Task<string> SendAsync(EvmTransactionRequest request, CancellationToken cancellationToken = default) =>
            throw new AssertionException("composed execution must journal before broadcast");
        public async Task<string> SendAsync(EvmTransactionRequest request,
            Func<EvmPreparedTransaction, CancellationToken, Task> onPrepared, CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            await onPrepared(new EvmPreparedTransaction(Harness.ClaimTxid, "0x02aa"), cancellationToken);
            if (FailAfterPrepareOnce)
            {
                FailAfterPrepareOnce = false;
                throw new IOException("process stopped before broadcast");
            }
            Broadcasts++;
            Assert.That(harness.SavedStates.Last(), Is.EqualTo((harness.Outgoing.Status, Harness.ClaimTxid, (string?)null)));
            return Harness.ClaimTxid;
        }

        public Task<string> ResumeAsync(EvmTransactionRequest request, EvmPreparedTransaction prepared,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            ResumedPrepared = prepared;
            Broadcasts++;
            return Task.FromResult(prepared.TransactionHash);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1_799_990_000);
    }
}
