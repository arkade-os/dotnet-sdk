using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Enums;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NSubstitute;

namespace NArk.Tests;

/// <summary>
/// Locks in the safety property the signer-rotation design (§8 / §2.1) relies on:
/// when a deprecated-signer coin is swept (regime 1, via SpendingService) or batched
/// (regime 3, via SimpleIntentScheduler), the fresh self/change output must re-land
/// under the CURRENT <see cref="ArkServerInfo.SignerKey"/> — never the old one carried
/// by the input contract. Both code paths funnel the self-output through
/// IArkadeAddressProvider.GetNextContract(SendToSelf, inputContracts); these tests
/// exercise that seam directly on <see cref="SingleKeyAddressProvider"/>.
/// </summary>
[TestFixture]
public class RotationSelfOutputCurrentSignerTests
{
    // A current ("rotated-to") server key and a distinct deprecated ("rotated-from") one.
    private static readonly OutputDescriptor CurrentServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest);

    private static readonly OutputDescriptor DeprecatedServerKey =
        KeyExtensions.ParseOutputDescriptor(
            "030192e796452d6df9697c280542e1560557bcf79a347d925895043136225c7cb4",
            Network.RegTest);

    [Test]
    public async Task GetNextContract_SendToSelf_DerivesUnderCurrentSigner_NotDeprecatedInputSigner()
    {
        var provider = MakeProvider(out _);

        // Input contract is funded under the OLD/deprecated signer (the rotation scenario).
        var userKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes())
            .ToOutputDescriptor(Network.RegTest);
        var oldContract = new ArkPaymentContract(DeprecatedServerKey, new Sequence(144), userKey);

        var (contract, entity) = await provider.GetNextContract(
            NextContractPurpose.SendToSelf,
            ContractActivityState.Active,
            inputContracts: [oldContract]);

        // The fresh self-output must bind the CURRENT signer, not the input's deprecated one.
        Assert.That(contract.Server, Is.Not.Null);
        Assert.That(contract.Server!.ToXOnlyPubKey().ToBytes(),
            Is.EqualTo(CurrentServerKey.ToXOnlyPubKey().ToBytes()),
            "Self-output server key must be the current SignerKey from server info.");
        Assert.That(contract.Server!.ToXOnlyPubKey().ToBytes(),
            Is.Not.EqualTo(DeprecatedServerKey.ToXOnlyPubKey().ToBytes()),
            "Self-output must not re-enroll under the deprecated input-contract signer.");

        // The persisted entity's server key must agree (it is what discovery/sweep reads back).
        Assert.That(entity.AdditionalData.TryGetValue("server", out var entityServer), Is.True);
        Assert.That(entityServer, Is.EqualTo(CurrentServerKey.ToString()));
    }

    [Test]
    public async Task GetNextContract_SendToSelf_TweakedAddress_UsesCurrentSigner()
    {
        var provider = MakeProvider(out _);

        var (contract, _) = await provider.GetNextContract(
            NextContractPurpose.SendToSelf,
            ContractActivityState.Active,
            inputContracts: null);

        // The Ark address the intent/spend output actually pays to carries the server key.
        var address = contract.GetArkAddress();
        Assert.That(address.ServerKey.ToBytes(),
            Is.EqualTo(CurrentServerKey.ToXOnlyPubKey().ToBytes()));
    }

    private static SingleKeyAddressProvider MakeProvider(out IClientTransport transport)
    {
        // Wallet's own key (the SingleKey account descriptor). Distinct from both server keys.
        // SingleKeyAddressProvider's ctor parses AccountDescriptor via raw OutputDescriptor.Parse,
        // which requires the explicit tr(...) form (a bare hex pubkey is not a valid descriptor).
        var walletKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        var wallet = new ArkWalletInfo(
            Id: "rotation-wallet",
            Secret: null,
            Destination: null,
            WalletType: WalletType.SingleKey,
            AccountDescriptor: $"tr({Convert.ToHexString(walletKey.ToBytes()).ToLowerInvariant()})",
            LastUsedIndex: 0);

        transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(MakeServerInfo());

        return new SingleKeyAddressProvider(transport, wallet, Network.RegTest, sweepingAddress: null);
    }

    private static ArkServerInfo MakeServerInfo()
    {
        var emptyMultisig = new NArk.Core.Scripts.NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>());
        return new ArkServerInfo(
            Dust: Money.Satoshis(546),
            SignerKey: CurrentServerKey,
            // The deprecated key is in the rotation map; the self-output must still use SignerKey.
            DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>()
            {
                { DeprecatedServerKey.ToXOnlyPubKey(), 0 }
            },
            Network: Network.RegTest,
            UnilateralExit: new Sequence(144),
            BoardingExit: new Sequence(144),
            ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
            ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
            CheckpointTapScript: new NArk.Core.Scripts.UnilateralPathArkTapScript(new Sequence(144), emptyMultisig),
            FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
            Digest: "server-digest-rotation");
    }
}
