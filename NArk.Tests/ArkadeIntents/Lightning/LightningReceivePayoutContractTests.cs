using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Where a receive swap's payout key comes from.
/// </summary>
/// <remarks>
/// An HD wallet is restored by scanning until <c>GapLimit</c> consecutive indices come back unused, so a
/// flow that mints an address per attempt reaches that limit twice as fast if every swap derives one of
/// its own. The negotiation is unreachable from a unit test — no quote written here can name the address
/// the client derives — but the payout is chosen before the solver is asked, which is what this pins.
/// </remarks>
[TestFixture]
public class LightningReceivePayoutContractTests
{
    [Test]
    public async Task WithNoContractSupplied_AFreshOneIsDerived()
    {
        var (client, contracts) = Build();

        await Negotiate(client, payout: null);

        await contracts.ReceivedWithAnyArgs(1).DeriveContract(default!, default, default, default, default);
    }

    [Test]
    public async Task WithAContractSupplied_TheSwapCostsNoHdIndex()
    {
        var (client, contracts) = Build();

        await Negotiate(client, Contract());

        await contracts.DidNotReceiveWithAnyArgs().DeriveContract(default!, default, default, default, default);
    }

    private static async Task Negotiate(LightningIntentsClient client, ArkContract? payout)
    {
        // The quote cannot survive the address comparison, and does not need to: the payout is already chosen.
        try
        {
            await client.ReceiveFromLightningAsync(
                "wallet-1", 50_000, Substitute.For<IRfqTransport>(), covclaimdPubKey: null,
                payoutContract: payout);
        }
        catch (Exception)
        {
        }
    }

    private static ArkContract Contract() => new ArkPaymentContract(
        ServerInfo.SignerKey, new Sequence(TimeSpan.FromSeconds(4096)),
        KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest));

    private static readonly ArkServerInfo ServerInfo = TestServerInfo.WithSeconds(4096);

    private static (LightningIntentsClient Client, IContractService Contracts) Build()
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(default).ReturnsForAnyArgs(ServerInfo);

        var contracts = Substitute.For<IContractService>();
        contracts.DeriveContract(default!, default, default, default, default).ReturnsForAnyArgs(Contract());

        return (new LightningIntentsClient(
            transport,
            contracts,
            Substitute.For<ISpendingService>(),
            Substitute.For<IArkadeIntentStorage>(),
            Substitute.For<IContractStorage>(),
            Substitute.For<IVtxoStorage>(),
            Substitute.For<IWalletProvider>()), contracts);
    }
}
