using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.Core;
using NArk.Transport.GrpcClient;
using NBitcoin;

namespace NArk.Tests.End2End.Arkade;

/// <summary>Exercises one real Arkade-to-EVM send against the isolated profile.</summary>
[TestFixture]
[Category("EvmSendCorridor")]
[Category("ArkadeIntents")]
public class EvmSendTests
{
    private const long SwapSats = 100_000;
    private const string Token = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
    private const string SwapContract = "0x00000000000000000000000000000000deadbeef";
    private const string ClientAddress = "0x3c44cdddb6a900fa2b585dd299e03d12fa4293bc";
    private static readonly TimeSpan SolverTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Proves the quote, both locks, claimFor delivery, and solver collection.</summary>
    [Test]
    [CancelAfter(240_000)]
    public async Task Send_FundsArkade_ThenClaimsTheRealErc20Swap(CancellationToken cancellationToken)
    {
        var solverUrl = Env("ARKADE_EVM_SEND_SOLVER_URL");
        if (solverUrl is null)
            Assert.Ignore("needs the real EVM profile; set ARKADE_EVM_SEND_SOLVER_URL");

        var arkdUrl = Env("ARKADE_EVM_ARKD_URL") ?? "http://localhost:27070";
        var rpcUrl = Env("ARKADE_EVM_RPC_URL") ?? "http://localhost:28545";
        var arkdContainer = Env("ARKADE_EVM_ARKD_CONTAINER") ?? "arkade-regtest-evm-e2e-arkd";
        var privateKey = ReadPrivateKey();
        var preimage = RandomNumberGenerator.GetBytes(32);
        try
        {
            var paymentHash = Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant();
            var transport = new GrpcClientTransport(arkdUrl);
            var serverInfo = await transport.GetServerInfoAsync(cancellationToken);
            Assert.That(serverInfo.Network, Is.EqualTo(Network.RegTest));
            var clientRefund = KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest);
            var refundPkScript = P2trScript(new Key());
            var refundAddress = ArkAddress.FromScriptPubKey(
                new Script(refundPkScript), serverInfo.SignerKey.ToXOnlyPubKey()).ToString(false);
            var request = EvmSendProfile.Request(
                SwapSats, paymentHash, ClientAddress, refundAddress,
                Convert.ToHexString(clientRefund.ToXOnlyPubKey().ToBytes()).ToLowerInvariant(), Token);

            using var solverHttp = new HttpClient();
            var rfq = new HttpRfqTransport(solverHttp, new Uri(solverUrl));
            var quote = await rfq.RequestEvmSendQuoteAsync(request, cancellationToken);
            var policy = Policy();
            using var rpcHttp = new HttpClient();
            var rpc = new EvmJsonRpcClient(rpcHttp, new Uri(rpcUrl));
            Assert.That(await rpc.GetChainIdAsync(cancellationToken), Is.EqualTo(new BigInteger(31_337)));
            var currentBlock = await rpc.GetBlockNumberAsync(cancellationToken);
            var values = EvmSendQuoteValidator.Validate(
                request, quote, policy, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), currentBlock);
            var lockup = EvmArkadeLockupValidator.Validate(
                request, quote, policy, serverInfo, clientRefund, refundPkScript,
                Env("ARKADE_EVM_EMULATOR_PUBKEY"));

            Assert.Multiple(() =>
            {
                Assert.That(lockup.HasEmulatorRefundPath, Is.True);
                Assert.That(lockup.Contract.GetArkAddress().ToString(false),
                    Is.EqualTo(quote.Profile!.LockupAddress));
                Assert.That(values.Amount, Is.EqualTo(quote.ToAtomicAmount));
            });

            var sender = new EvmLocalTransactionSender(rpc, privateKey, new EvmTransactionSenderOptions
            {
                ExpectedSenderAddress = ClientAddress,
                MaxFeePerGasWei = 100_000_000_000,
                MaxPriorityFeePerGasWei = 10_000_000_000,
                MaxGasLimit = 500_000,
            });
            var chain = new EvmSwapChainClient(rpc, sender, policy);

            await FundArkadeAsync(
                arkdContainer, lockup.Contract.GetArkAddress().ToString(false), SwapSats, cancellationToken);
            var lockupScript = lockup.Contract.GetScriptPubKey().ToHex();
            var funded = await WaitForVtxoAsync(transport, lockupScript, cancellationToken);
            Assert.That(funded.Amount, Is.EqualTo((ulong)SwapSats));
            TestContext.Progress.WriteLine(JsonSerializer.Serialize(new
            {
                stage = "arkade_funded",
                request.RfqId,
                arkade_funding_txid = funded.TransactionId,
                arkade_amount_sats = SwapSats.ToString(CultureInfo.InvariantCulture),
            }));
            var proof = await WaitForLockProofAsync(chain, values, cancellationToken);
            var claim = await chain.ClaimForAsync(values, preimage, cancellationToken);
            try
            {
                Assert.Multiple(() =>
                {
                    Assert.That(claim.DeliveredAmount, Is.EqualTo(values.Amount));
                    Assert.That(claim.Preimage, Is.EqualTo(preimage));
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(claim.Preimage);
            }

            var consumed = !Erc20SwapCodec.ReadSwapsResult(await rpc.CallAsync(
                SwapContract, Erc20SwapCodec.SwapsCall(values), cancellationToken: cancellationToken));
            Assert.That(consumed, Is.True, "the exact ERC20Swap key is consumed after claimFor");
            TestContext.Progress.WriteLine(JsonSerializer.Serialize(new
            {
                stage = "evm_claimed",
                request.RfqId,
                evm_claim_txid = claim.TransactionHash,
                erc20_amount = values.Amount.ToString(CultureInfo.InvariantCulture),
                erc20_transfer_proven = true,
                swap_consumed = consumed,
            }));
            var settled = await WaitForSpentVtxoAsync(transport, lockupScript, cancellationToken);

            TestContext.Progress.WriteLine(JsonSerializer.Serialize(new
            {
                stage = "route_settled",
                request.RfqId,
                payment_hash = paymentHash,
                arkade_lockup_address = quote.Profile!.LockupAddress,
                arkade_funding_txid = funded.TransactionId,
                arkade_spending_txid = settled.SpentByTransactionId,
                arkade_amount_sats = SwapSats.ToString(CultureInfo.InvariantCulture),
                erc20_amount = values.Amount.ToString(CultureInfo.InvariantCulture),
                evm_timeout_block = values.TimeoutBlock.ToString(CultureInfo.InvariantCulture),
                evm_lock_key = Convert.ToHexString(Erc20SwapCodec.SwapKey(values)).ToLowerInvariant(),
                evm_proven_at_block = proof.ProvenAtBlock.ToString(CultureInfo.InvariantCulture),
                evm_claim_txid = claim.TransactionHash,
                erc20_transfer_proven = true,
                swap_consumed = consumed,
            }));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(preimage);
        }
    }

    private static EvmSendPolicy Policy() => new()
    {
        ChainId = 31_337,
        TokenAddress = Token,
        SwapContractAddress = SwapContract,
        FastestSecondsPerBlock = 1,
        SlowestSecondsPerBlock = 1,
        MinConfirmations = 1,
        MinAgeSeconds = 1,
        MinimumClaimWindowSeconds = 1_700,
        RequireEmulatorRefundPath = true,
    };

    private static byte[] ReadPrivateKey()
    {
        var value = Env("ARKADE_EVM_CLIENT_PRIVATE_KEY");
        if (value is null)
            Assert.Fail("ARKADE_EVM_CLIENT_PRIVATE_KEY must provide the profile's funded gas-payer key");
        try
        {
            var key = Convert.FromHexString(value!);
            if (key.Length == 32)
                return key;
        }
        catch (FormatException)
        {
        }
        throw new InvalidOperationException("ARKADE_EVM_CLIENT_PRIVATE_KEY is not a 32-byte hex key");
    }

    private static byte[] P2trScript(Key key) =>
        [0x51, 0x20, .. key.PubKey.TaprootInternalKey.ToBytes()];

    private static async Task FundArkadeAsync(
        string container, string address, long sats, CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap("docker").WithArguments([
                "exec", container, "ark", "send", "--to", address, "--amount",
                sats.ToString(CultureInfo.InvariantCulture), "--password",
                Env("ARKADE_EVM_ARKD_PASSWORD") ?? "secret",
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"Arkade funding failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
    }

    private static async Task<ArkVtxo> WaitForVtxoAsync(
        GrpcClientTransport transport, string script, CancellationToken cancellationToken) =>
        await WaitAsync(async () => await GetVtxoAsync(transport, script, cancellationToken),
            "Arkade lockup funding", cancellationToken);

    private static async Task<ArkVtxo> WaitForSpentVtxoAsync(
        GrpcClientTransport transport, string script, CancellationToken cancellationToken) =>
        await WaitAsync(async () =>
        {
            var vtxo = await GetVtxoAsync(transport, script, cancellationToken);
            return vtxo?.SpentByTransactionId is { Length: > 0 } ? vtxo : null;
        }, "solver Arkade claim", cancellationToken);

    private static async Task<ArkVtxo?> GetVtxoAsync(
        GrpcClientTransport transport, string script, CancellationToken cancellationToken)
    {
        await foreach (var vtxo in transport.GetVtxoByScriptsAsSnapshot(
            new HashSet<string> { script }, cancellationToken))
            return vtxo;
        return null;
    }

    private static async Task<EvmLockProof> WaitForLockProofAsync(
        EvmSwapChainClient chain, Erc20SwapValues values, CancellationToken cancellationToken)
    {
        EvmSwapProofException? last = null;
        var deadline = DateTimeOffset.UtcNow + SolverTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return await chain.ProveLockAsync(values, cancellationToken);
            }
            catch (EvmSwapProofException exception)
            {
                last = exception;
            }
            await Task.Delay(1_000, cancellationToken);
        }
        throw new TimeoutException("solver EVM lock was not proven before the deadline", last);
    }

    private static async Task<T> WaitAsync<T>(
        Func<Task<T?>> probe, string operation, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + SolverTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe() is { } value)
                return value;
            await Task.Delay(1_000, cancellationToken);
        }
        throw new TimeoutException($"{operation} did not complete before the deadline");
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
