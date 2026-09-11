using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.Arkade.Emulator;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.Core.Services;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End.Arkade;

[TestFixture]
[Category("ArkadeScript")]
public class WatchOnlyVhtlcTests
{
    [TestCase("lightning")]
    [TestCase("onchain")]
    [TestCase("refund")]
    public async Task SplitLockup_EmulatorSubmitsWithoutWalletSigner(string path)
    {
        var w = await FundedWalletHelper.GetFundedWallet();
        await using var sync = w.vtxoSync;
        var server = await w.clientTransport.GetServerInfoAsync();
        var payout = await w.contractService.DeriveContract(w.walletIdentifier, NextContractPurpose.Receive);
        var emulator = new EmulatorClient(new HttpClient(),
            Options.Create(new EmulatorClientOptions { ServerUrl = "http://localhost:7073" }));
        var emulatorInfo = await emulator.GetInfoAsync();
        var emulatorKey = LightningCorridor.NormalizeToXOnly(Convert.FromHexString(emulatorInfo.SignerPubkey));
        var preimage = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var hash = new uint160(NBitcoin.Crypto.Hashes.RIPEMD160(NBitcoin.Crypto.Hashes.SHA256(preimage), 32), false);
        var refund = path == "refund";
        var locktime = refund ? 500_000_000u : checked((uint)DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds());
        var sender = KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), server.Network);
        var receiver = KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), server.Network);
        var contract = new VHTLCv2Contract(server.SignerKey, sender, receiver, hash, new LockTime(locktime),
            server.UnilateralExit, server.UnilateralExit, new Sequence(server.UnilateralExit.Value + 1),
            new VHTLCv2NonInteractiveClaim(payout.GetScriptPubKey().ToBytes(), emulatorKey),
            new VHTLCv2NonInteractiveRefund(payout.GetScriptPubKey().ToBytes(), emulatorKey, WithoutReceiver: true));
        await w.contracts.SaveContract(contract.ToEntity(w.walletIdentifier));

        await ArkadeFaucet.Fund(contract.GetArkAddress().ToString(false), 30_000);
        await ArkadeFaucet.Fund(contract.GetArkAddress().ToString(false), 70_000);
        var lockups = await WaitForOutputs(w.clientTransport, contract.GetScriptPubKey().ToHex(), 2);
        foreach (var vtxo in lockups) await w.vtxoStorage.UpsertVtxo(vtxo);
        var intents = new InMemoryArkadeIntentStorage();
        var intent = new ArkadeSwapIntent
        {
            Id = Guid.NewGuid().ToString("N"), WalletId = w.walletIdentifier,
            Type = refund ? ArkadeSwapIntentType.BtcToLightning
                : path == "onchain" ? ArkadeSwapIntentType.OnchainToBtc : ArkadeSwapIntentType.LightningToBtc,
            OfferAmount = Money.Satoshis(100_000), WantAmount = Money.Satoshis(100_000),
            Status = ArkadeSwapIntentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
            SwapPkScript = contract.GetScriptPubKey().ToHex(), SwapAddress = contract.GetArkAddress().ToString(false),
            RefundLocktime = locktime, Metadata = refund ? [] : new Dictionary<string, string>
            {
                [ArkadeSwapMetadataKeys.Preimage] = Convert.ToHexString(preimage)
            }
        };
        await intents.SaveArkadeSwapIntent(intent);
        var watchOnly = new WatchOnlyProvider();
        var spending = new SpendingService(w.vtxoStorage, w.contracts, new CoinService(w.clientTransport, w.contracts, []),
            watchOnly, w.contractService, w.clientTransport, new NArk.Core.CoinSelector.DefaultCoinSelector(),
            w.safetyService, TestStorage.CreateIntentStorage(), [],
            extensionPacketProviders: [new ArkadeEmulatorPacketProvider()],
            submitHandlers: [new ArkadeEmulatorSpendSubmitter(emulator, new PrevArkTxProvider(w.clientTransport))]);
        var lightning = new LightningIntentsClient(w.clientTransport, w.contractService, spending, intents,
            w.contracts, w.vtxoStorage, watchOnly);

        var result = path switch
        {
            "refund" => await lightning.RefundNonInteractiveAsync(intent.Id),
            "onchain" => await new OnchainIntentsClient(w.clientTransport, w.contractService, spending, intents,
                w.contracts, w.vtxoStorage, watchOnly, null!).ClaimNonInteractiveAsync(intent.Id),
            _ => await lightning.ClaimNonInteractiveAsync(intent.Id)
        };

        Assert.That(result.Status, Is.EqualTo(refund ? ArkadeSwapIntentStatus.Cancelled : ArkadeSwapIntentStatus.Fulfilled));
        Assert.That(watchOnly.SignerRequests, Is.Zero);
        var paid = await WaitForOutputs(w.clientTransport, payout.GetScriptPubKey().ToHex(), 2, result.SpentTxid);
        Assert.That(paid.Select(v => v.Amount), Is.EquivalentTo(new ulong[] { 30_000, 70_000 }));
        var submitted = PSBT.Parse((await w.clientTransport.GetVirtualTxsAsync([result.SpentTxid!])).Single(), server.Network);
        var tx = submitted.GetGlobalTransaction();
        Assert.That(tx.Outputs.Take(2).All(o => o.ScriptPubKey == payout.GetScriptPubKey()), Is.True);
        Assert.That(tx.Outputs.Take(2).Select(o => o.Value.Satoshi), Is.EquivalentTo(new long[] { 30_000, 70_000 }));
    }

    private static async Task<ArkVtxo[]> WaitForOutputs(NArk.Core.Transport.IClientTransport transport,
        string script, int count, string? txid = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var found = new List<ArkVtxo>();
            await foreach (var vtxo in transport.GetVtxoByScriptsAsSnapshot(new HashSet<string> { script }))
                if (!vtxo.IsSpent() && !vtxo.Swept && (txid is null || vtxo.TransactionId == txid)) found.Add(vtxo);
            if (found.Count == count) return found.ToArray();
            await Task.Delay(250);
        }
        throw new TimeoutException($"Expected {count} indexed outputs at {script}.");
    }

    private sealed class WatchOnlyProvider : IWalletProvider
    {
        internal int SignerRequests { get; private set; }
        public Task<IArkadeWalletSigner?> GetSignerAsync(string identifier, CancellationToken cancellationToken = default)
        {
            SignerRequests++;
            return Task.FromResult<IArkadeWalletSigner?>(null);
        }

        public Task<IArkadeAddressProvider?> GetAddressProviderAsync(string identifier, CancellationToken cancellationToken = default) =>
            Task.FromResult<IArkadeAddressProvider?>(null);
    }
}
