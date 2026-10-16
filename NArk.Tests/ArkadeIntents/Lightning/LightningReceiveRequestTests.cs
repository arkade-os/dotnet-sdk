using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>What a Lightning receive asks the solver for, before any quote comes back.</summary>
[TestFixture]
public class LightningReceiveRequestTests
{
    [Test]
    public void AnExactInRequest_CarriesNoClaimPacket()
    {
        Assert.That(RequestSent(RfqAmountSide.From).Profile.ClaimPacket, Is.Null);
    }

    [Test]
    public void AnExactOutRequest_BindsItsAmountIntoTheClaimPacket()
    {
        var packet = Convert.FromBase64String(RequestSent(RfqAmountSide.To).Profile.ClaimPacket!);

        Assert.That(Convert.ToHexString(packet[^11..]).ToLowerInvariant(),
            Is.EqualTo("040008" + 50_000UL.ToString("x16")));
    }

    [Test]
    public void ANonPositiveAmount_IsRefusedBeforeTheSolverIsAsked()
    {
        var rfq = Substitute.For<IRfqTransport>();

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Client().ReceiveFromLightningAsync("wallet-1", -1, rfq, new Key().PubKey.ToHex()));
        Assert.That(rfq.ReceivedCalls(), Is.Empty);
    }

    private static RfqRequest<LightningReceiveRequestProfile> RequestSent(RfqAmountSide side)
    {
        var rfq = Substitute.For<IRfqTransport>();
        Assert.CatchAsync(() => Client().ReceiveFromLightningAsync(
            "wallet-1", 50_000, rfq, new Key().PubKey.ToHex(), amountSide: side));

        var call = rfq.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IRfqTransport.RequestQuoteAsync));
        return (RfqRequest<LightningReceiveRequestProfile>)call.GetArguments()[0]!;
    }

    private static LightningIntentsClient Client()
    {
        var info = TestServerInfo.WithSeconds(4096);
        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(default).ReturnsForAnyArgs(info);
        var contracts = Substitute.For<IContractService>();
        contracts.DeriveContract(default!, default, default, default, default).ReturnsForAnyArgs(new ArkPaymentContract(
            info.SignerKey, new Sequence(TimeSpan.FromSeconds(4096)),
            KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest)));
        // A bare substitute signer signs nothing; with none the preimage is random, which a request is happy with.
        var wallets = Substitute.For<IWalletProvider>();
        wallets.GetSignerAsync(default!, default).ReturnsForAnyArgs((IArkadeWalletSigner?)null);
        return new LightningIntentsClient(transport, contracts, Substitute.For<ISpendingService>(),
            Substitute.For<IArkadeIntentStorage>(), Substitute.For<IContractStorage>(),
            Substitute.For<IVtxoStorage>(), wallets);
    }
}
