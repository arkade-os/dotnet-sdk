using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Batches;
using NArk.Core.Enums;
using NArk.Core.Helpers;
using NArk.Core.Transport;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.Batches;

/// <summary>
/// <see cref="BatchSession.InitializeAsync"/> is where the operator-declared expiry is bounded, and
/// <c>BatchManagementService</c> calls it before confirming registration — so a rejected batch is
/// never confirmed and no signing state is built.
/// </summary>
[TestFixture]
public class BatchSessionExpiryTests
{
    [Test]
    public void InitializeAsync_RejectsUnsafeExpiry_WithoutContactingTheServer()
    {
        var clientTransport = Substitute.For<IClientTransport>();

        // One block: the operator could sweep the batch output before any unilateral exit completes.
        var session = BuildSession(clientTransport, declaredExpiry: 1);

        Assert.ThrowsAsync<InvalidBatchExpiryException>(() => session.InitializeAsync());

        // The expiry is bounded before the round-trip, so a rejected batch costs nothing.
        clientTransport.DidNotReceive().GetServerInfoAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void ProcessEventAsync_Throws_WhenInitializeWasRejected()
    {
        var session = BuildSession(Substitute.For<IClientTransport>(), declaredExpiry: 1);

        Assert.ThrowsAsync<InvalidBatchExpiryException>(() => session.InitializeAsync());

        // No sweep root was derived, so nothing downstream can proceed on the rejected batch.
        Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ProcessEventAsync(new TreeTxEvent("batch-1", 0,
                new Dictionary<uint, string>(), [], "tx", "txid")));
    }

    private static BatchSession BuildSession(IClientTransport clientTransport, long declaredExpiry)
    {
        var network = Network.Main;
        var intent = new ArkIntent(
            IntentTxId: "intent-tx",
            IntentId: "intent-1",
            WalletId: "wallet-1",
            State: ArkIntentState.WaitingForBatch,
            ValidFrom: null,
            ValidUntil: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            RegisterProof: string.Empty,
            RegisterProofMessage:
            """{"type":"register","onchain_output_indexes":[],"cosigners_public_keys":[]}""",
            DeleteProof: string.Empty,
            DeleteProofMessage: "{}",
            BatchId: null,
            CommitmentTransactionId: null,
            CancellationReason: null,
            IntentVtxos: [],
            SignerDescriptor: "tr(0000000000000000000000000000000000000000000000000000000000000001)");

        var builder = new TransactionHelpers.ArkTransactionBuilder(
            clientTransport,
            Substitute.For<ISafetyService>(),
            Substitute.For<IWalletProvider>(),
            Substitute.For<IIntentStorage>());

        return new BatchSession(
            clientTransport,
            Substitute.For<IWalletProvider>(),
            builder,
            network,
            intent,
            [],
            new BatchStartedEvent("batch-1", BatchExpiryPolicy.Encode(declaredExpiry), [], declaredExpiry));
    }
}
