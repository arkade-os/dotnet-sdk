using System.Text.Json;
using NArk.Abstractions.Extensions;
using NArk.Core;
using NArk.Core.Extensions;
using NArk.Core.Scripts;
using NArk.Swaps.Boltz.Models.Restore;
using NArk.Swaps.Services;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.Swaps;

/// <summary>
/// Unit tests for the polymorphic <c>claimDetails</c>/<c>refundDetails</c> restore models and
/// <see cref="SwapsManagementService.ReconstructChainVhtlcContract"/>, added when Boltz's
/// <c>/v2/swap/restore</c> support was extended to chain swaps (previously only
/// submarine/reverse were handled). Covers the currency-agnostic building block other chain-swap
/// providers (e.g. an EVM leg) reuse to restore their own Ark-side lockup.
/// </summary>
[TestFixture]
public class ChainSwapRestoreTests
{
    private static readonly Network Net = Network.RegTest;

    private static OutputDescriptor Desc() =>
        KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Net);

    private static ArkServerInfo ServerInfo(OutputDescriptor signer) => new(
        Dust: Money.Satoshis(330),
        SignerKey: signer,
        DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance),
        Network: Net,
        UnilateralExit: new Sequence(144),
        BoardingExit: new Sequence(144),
        ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Net),
        ForfeitPubKey: signer.Extract().XOnlyPubKey,
        CheckpointTapScript: new UnilateralPathArkTapScript(
            new Sequence(144), new NofNMultisigTapScript(Array.Empty<ECXOnlyPubKey>())),
        FeeTerms: new ArkOperatorFeeTerms("0", "0", "0", "0", "0"),
        Digest: "");

    private static SwapTreeLeaf CltvLeaf(uint locktime) =>
        new() { Version = 192, Output = new Script(Op.GetPushOp(locktime), OpcodeType.OP_CHECKLOCKTIMEVERIFY).ToHex() };

    private static SwapTreeLeaf CsvLeaf(uint delay) =>
        new() { Version = 192, Output = new Script(Op.GetPushOp(delay), OpcodeType.OP_CHECKSEQUENCEVERIFY).ToHex() };

    private static SwapTree MakeTree() => new()
    {
        RefundWithoutBoltzLeaf = CltvLeaf(500_000),
        UnilateralClaimLeaf = CsvLeaf(10),
        UnilateralRefundLeaf = CsvLeaf(20),
        UnilateralRefundWithoutBoltzLeaf = CsvLeaf(30),
    };

    private static UtxoSwapDetails MakeUtxoDetails(OutputDescriptor serverSigner, int keyIndex = 0) => new()
    {
        Tree = MakeTree(),
        KeyIndex = keyIndex,
        LockupAddress = "tark1qtest",
        ServerPublicKey = Convert.ToHexString(serverSigner.Extract().XOnlyPubKey.ToBytes()).ToLowerInvariant(),
        Amount = 50_000,
    };

    private static string PreimageHash() =>
        Convert.ToHexString(NBitcoin.Crypto.Hashes.SHA256(new byte[32])).ToLowerInvariant();

    // ── Polymorphic JSON deserialization ─────────────────────────────────────

    [Test]
    public void RestorableSwap_DeserializesUtxoClaimDetails()
    {
        const string json = """
            {
              "id": "swap1", "type": "chain", "status": "swap.created", "createdAt": 1700000000,
              "from": "ARK", "to": "BTC",
              "refundDetails": {
                "type": "utxo", "tree": { }, "keyIndex": 0,
                "lockupAddress": "tark1qtest", "serverPublicKey": "aa", "timeoutBlockHeight": 100
              }
            }
            """;

        var restored = JsonSerializer.Deserialize<RestorableSwap>(json)!;

        Assert.That(restored.IsChainSwap, Is.True);
        Assert.That(restored.RefundDetails, Is.TypeOf<UtxoSwapDetails>());
        Assert.That(((UtxoSwapDetails)restored.RefundDetails!).LockupAddress, Is.EqualTo("tark1qtest"));
        Assert.That(restored.ClaimDetails, Is.Null);
    }

    [Test]
    public void RestorableSwap_MissingTypeDiscriminator_DefaultsToUtxo()
    {
        // Verified against a real Boltz instance: reverse/submarine swaps' claimDetails/
        // refundDetails omit "type" entirely — only chain-swap legs reliably send it. The
        // built-in [JsonPolymorphic] converter throws NotSupportedException on this; the
        // custom SwapDetailsJsonConverter must default to UtxoSwapDetails instead.
        const string json = """
            {
              "id": "swap-reverse", "type": "reverse", "status": "transaction.mempool", "createdAt": 1700000000,
              "from": "BTC", "to": "ARK",
              "claimDetails": {
                "tree": { }, "keyIndex": 0,
                "lockupAddress": "tark1qtest", "serverPublicKey": "aa", "timeoutBlockHeight": 100
              }
            }
            """;

        var restored = JsonSerializer.Deserialize<RestorableSwap>(json)!;

        Assert.That(restored.ClaimDetails, Is.TypeOf<UtxoSwapDetails>());
        Assert.That(((UtxoSwapDetails)restored.ClaimDetails!).LockupAddress, Is.EqualTo("tark1qtest"));
    }

    [Test]
    public void RestorableSwap_DeserializesEvmClaimDetails()
    {
        const string json = """
            {
              "id": "swap2", "type": "chain", "status": "swap.created", "createdAt": 1700000000,
              "from": "ARK", "to": "TBTC",
              "claimDetails": {
                "type": "evm", "contractAddress": "0xabc", "claimAddress": "0xdef",
                "timeoutBlockHeight": 200
              }
            }
            """;

        var restored = JsonSerializer.Deserialize<RestorableSwap>(json)!;

        Assert.That(restored.ClaimDetails, Is.TypeOf<EvmSwapDetails>());
        var evm = (EvmSwapDetails)restored.ClaimDetails!;
        Assert.That(evm.ContractAddress, Is.EqualTo("0xabc"));
        Assert.That(evm.ClaimAddress, Is.EqualTo("0xdef"));
    }

    // ── ReconstructChainVhtlcContract ─────────────────────────────────────────

    [Test]
    public void ReconstructChainVhtlcContract_WeAreReceiver_UsesClaimDetailsAndServerIsSender()
    {
        var server = Desc();
        var us = Desc();
        var serverInfo = ServerInfo(server);

        var restored = new RestorableSwap
        {
            Id = "swap-chain-btc-to-ark",
            Type = "chain",
            Status = "transaction.confirmed",
            CreatedAt = 1_700_000_000,
            From = "BTC",
            To = "ARK",
            PreimageHash = PreimageHash(),
            ClaimDetails = MakeUtxoDetails(server),
        };

        var contract = SwapsManagementService.ReconstructChainVhtlcContract(
            restored, weAreReceiver: true, serverInfo, [us]);

        Assert.That(contract, Is.Not.Null);
        Assert.That(contract!.Receiver, Is.EqualTo(us));
        Assert.That(contract.Sender.Extract().XOnlyPubKey.ToBytes(), Is.EqualTo(server.Extract().XOnlyPubKey.ToBytes()));
    }

    [Test]
    public void ReconstructChainVhtlcContract_WeAreSender_UsesRefundDetailsAndServerIsReceiver()
    {
        var server = Desc();
        var us = Desc();
        var serverInfo = ServerInfo(server);

        var restored = new RestorableSwap
        {
            Id = "swap-chain-ark-to-btc",
            Type = "chain",
            Status = "transaction.confirmed",
            CreatedAt = 1_700_000_000,
            From = "ARK",
            To = "BTC",
            PreimageHash = PreimageHash(),
            RefundDetails = MakeUtxoDetails(server),
        };

        var contract = SwapsManagementService.ReconstructChainVhtlcContract(
            restored, weAreReceiver: false, serverInfo, [us]);

        Assert.That(contract, Is.Not.Null);
        Assert.That(contract!.Sender, Is.EqualTo(us));
        Assert.That(contract.Receiver.Extract().XOnlyPubKey.ToBytes(), Is.EqualTo(server.Extract().XOnlyPubKey.ToBytes()));
    }

    [Test]
    public void ReconstructChainVhtlcContract_RequestedSideIsEvmTyped_ReturnsNull()
    {
        var server = Desc();
        var us = Desc();
        var serverInfo = ServerInfo(server);

        // ChainArkToEvm shape: refundDetails is our own Ark lockup (utxo), claimDetails is the
        // EVM side we'd claim — asking for weAreReceiver=true here should find an EVM-typed
        // claimDetails and safely return null rather than throw.
        var restored = new RestorableSwap
        {
            Id = "swap-chain-ark-to-evm",
            Type = "chain",
            Status = "transaction.confirmed",
            CreatedAt = 1_700_000_000,
            From = "ARK",
            To = "TBTC",
            PreimageHash = PreimageHash(),
            RefundDetails = MakeUtxoDetails(server),
            ClaimDetails = new EvmSwapDetails { ContractAddress = "0xabc", TimeoutBlockHeight = 200 },
        };

        Assert.That(
            SwapsManagementService.ReconstructChainVhtlcContract(restored, weAreReceiver: true, serverInfo, [us]),
            Is.Null);

        // The correct side (refundDetails, since we locked the Ark leg) reconstructs fine.
        Assert.That(
            SwapsManagementService.ReconstructChainVhtlcContract(restored, weAreReceiver: false, serverInfo, [us]),
            Is.Not.Null);
    }

    [Test]
    public void ReconstructChainVhtlcContract_NoPreimageHash_ReturnsNull()
    {
        var server = Desc();
        var us = Desc();
        var serverInfo = ServerInfo(server);

        var restored = new RestorableSwap
        {
            Id = "swap-no-hash",
            Type = "chain",
            Status = "transaction.confirmed",
            CreatedAt = 1_700_000_000,
            From = "BTC",
            To = "ARK",
            PreimageHash = null,
            ClaimDetails = MakeUtxoDetails(server),
        };

        Assert.That(
            SwapsManagementService.ReconstructChainVhtlcContract(restored, weAreReceiver: true, serverInfo, [us]),
            Is.Null);
    }
}
