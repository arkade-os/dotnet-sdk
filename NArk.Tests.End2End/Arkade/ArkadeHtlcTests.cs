using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.Arkade.Program.Models;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.End2End.Arkade;

/// <summary>
/// End-to-end covenant tests against a live arkd + emulator (docker `--profile emulator`).
/// Ports the ts-sdk <c>e2e/arkade-htlc.test.ts</c>: an HTLC whose <c>claim</c> path is gated by a
/// HASH160 preimage and whose <c>refund</c> path is gated by a CLTV timelock, with an ArkadeScript
/// covenant forcing the spend to pay a fixed amount to a fixed receiver. Exercises the full covenant
/// spend path wired this session: transformer call-time witness → emulator OP_RETURN packet →
/// <see cref="ArkadeEmulatorSpendSubmitter"/> co-sign.
/// </summary>
[TestFixture]
public class ArkadeHtlcTests
{
    private static readonly Uri EmulatorEndpoint = new("http://localhost:7073");

    private static readonly byte[] Preimage = Enumerable.Repeat((byte)0x42, 32).ToArray();
    // HASH160(Preimage) = RIPEMD160(SHA256(Preimage))
    private static readonly byte[] PreimageHash = Convert.FromHexString("8739f40ec4dbf569dcb38134c6e7310908566981");
    private const long ContractAmount = 10_000;

    // Covenant body: output 0 must pay exactly $amount to the taproot witness program $receiver.
    // The leading DUP consumes the output index pushed by the witness [0]; INSPECTOUTPUTSCRIPTPUBKEY
    // yields (version, program) so we check version == 1 then program == $receiver.
    private static IReadOnlyList<AsmToken> PayTo() =>
    [
        "DUP", "INSPECTOUTPUTSCRIPTPUBKEY", 1, "EQUALVERIFY",
        "$receiver", "EQUALVERIFY",
        "INSPECTOUTPUTVALUE", "$amount", "EQUAL",
    ];

    [Test]
    public async Task Claim_EmulatorCoSignsWhenPreimageAndCovenantPass()
    {
        var ctx = await SetUpAsync();
        var (receiverAddress, receiverProgram) = await DeriveReceiver(ctx);

        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Params =
            [
                new TypedInput { Name = "server", Type = InputType.Pubkey },
                new TypedInput { Name = "hash", Type = InputType.Hash },
                new TypedInput { Name = "receiver", Type = InputType.Pubkey },
                new TypedInput { Name = "amount", Type = InputType.Int },
            ],
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["claim"] = new()
                {
                    Inputs = [new TypedInput { Name = "preimage", Type = InputType.Bytes }],
                    Tapscript = new TapscriptSegment
                    {
                        Signers = ["$server"],
                        Asm = ["HASH160", "$hash", "EQUAL"],
                        Witness = ["preimage"],
                    },
                    ScriptSegment = new ArkadeScriptSegment { Asm = PayTo(), Witness = [0] },
                },
            },
        };

        var contract = new ArkProgramContract(ctx.Server, program,
            new Dictionary<string, AsmToken>
            {
                ["hash"] = PreimageHash,
                ["receiver"] = receiverProgram,
                ["amount"] = ContractAmount,
            },
            user: null, emulatorKey: ctx.EmulatorKey);

        var vtxo = await FundAndWait(ctx, contract);

        var coin = await new ArkProgramContractTransformer(ctx.WalletProvider)
            .Transform(ctx.WalletId, contract, vtxo, "claim",
                new Dictionary<string, AsmToken> { ["preimage"] = Preimage });

        var txid = await Spend(ctx, coin,
            [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(ContractAmount), receiverAddress)]);

        Assert.That(txid, Is.Not.EqualTo(uint256.Zero));
    }

    [Test]
    public async Task Refund_EmulatorCoSignsWhenCltvSatisfiedAndCovenantPasses()
    {
        var ctx = await SetUpAsync();
        var (receiverAddress, receiverProgram) = await DeriveReceiver(ctx);

        var program = new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Params =
            [
                new TypedInput { Name = "server", Type = InputType.Pubkey },
                new TypedInput { Name = "receiver", Type = InputType.Pubkey },
                new TypedInput { Name = "amount", Type = InputType.Int },
            ],
            Functions = new Dictionary<string, ArkadeFunction>
            {
                ["refund"] = new()
                {
                    // genesis-relative CLTV, always satisfied in regtest.
                    Tapscript = new TapscriptSegment { Signers = ["$server"], Cltv = new LockTime(500_000_000) },
                    ScriptSegment = new ArkadeScriptSegment { Asm = PayTo(), Witness = [0] },
                },
            },
        };

        var contract = new ArkProgramContract(ctx.Server, program,
            new Dictionary<string, AsmToken> { ["receiver"] = receiverProgram, ["amount"] = ContractAmount },
            user: null, emulatorKey: ctx.EmulatorKey);

        var vtxo = await FundAndWait(ctx, contract);

        var coin = await new ArkProgramContractTransformer(ctx.WalletProvider)
            .Transform(ctx.WalletId, contract, vtxo, "refund");


        var txid = await Spend(ctx, coin,
            [new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(ContractAmount), receiverAddress)]);

        Assert.That(txid, Is.Not.EqualTo(uint256.Zero));
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private sealed record Ctx(
        string WalletId,
        IWalletProvider WalletProvider,
        IVtxoStorage VtxoStorage,
        IContractStorage Contracts,
        IContractService ContractService,
        IClientTransport Transport,
        ISafetyService Safety,
        EmulatorClient Emulator,
        OutputDescriptor Server,
        ECXOnlyPubKey EmulatorKey);

    private static async Task<Ctx> SetUpAsync()
    {
        var w = await FundedWalletHelper.GetFundedWallet();

        var emulator = new EmulatorClient(new HttpClient(),
            Options.Create(new EmulatorClientOptions { ServerUrl = EmulatorEndpoint.ToString() }));
        var info = await emulator.GetInfoAsync();
        // signerPubkey is 33-byte compressed; drop the parity byte for the x-only key.
        var emulatorKey = ECXOnlyPubKey.Create(Convert.FromHexString(info.SignerPubkey)[1..]);

        var serverInfo = await w.clientTransport.GetServerInfoAsync();

        return new Ctx(w.walletIdentifier, w.walletProvider, w.vtxoStorage, w.contracts,
            w.contractService, w.clientTransport, w.safetyService, emulator, serverInfo.SignerKey, emulatorKey);
    }

    /// <summary>Derive a receive address and return it plus its 32-byte taproot program (for <c>$receiver</c>).</summary>
    private static async Task<(ArkAddress address, byte[] witnessProgram)> DeriveReceiver(Ctx ctx)
    {
        var contract = await ctx.ContractService.DeriveContract(ctx.WalletId, NextContractPurpose.Receive);
        var address = contract.GetArkAddress();
        // P2TR scriptPubKey is OP_1 PUSH32 <program>; the 32-byte program is the covenant's $receiver.
        var program = address.ScriptPubKey.ToBytes()[2..];
        return (address, program);
    }

    private static async Task<ArkVtxo> FundAndWait(Ctx ctx, ArkProgramContract contract)
    {
        await DockerHelper.SendArkdNoteTo(contract.GetArkAddress().ToString(false), ContractAmount);

        var script = contract.GetArkAddress().ScriptPubKey.ToHex();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await foreach (var vtxo in ctx.Transport.GetVtxoByScriptsAsSnapshot(new HashSet<string> { script }))
            {
                if (!vtxo.Swept && string.IsNullOrEmpty(vtxo.SpentByTransactionId))
                    return vtxo;
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"No spendable VTXO appeared at {script} within 30s.");
    }

    private static async Task<uint256> Spend(Ctx ctx, ArkCoin coin, ArkTxOut[] outputs)
    {
        var coinService = new CoinService(ctx.Transport, ctx.Contracts,
            [new ArkProgramContractTransformer(ctx.WalletProvider)]);

        var spendingService = new SpendingService(
            ctx.VtxoStorage, ctx.Contracts, coinService, ctx.WalletProvider,
            ctx.ContractService, ctx.Transport, new NArk.Core.CoinSelector.DefaultCoinSelector(),
            ctx.Safety, TestStorage.CreateIntentStorage(),
            postSpendEventHandlers: [], logger: null,
            extensionPacketProviders: [new ArkadeEmulatorPacketProvider()],
            submitHandlers: [new ArkadeEmulatorSpendSubmitter(ctx.Emulator)]);

        return await spendingService.Spend(ctx.WalletId, [coin], outputs);
    }
}
