using System.Numerics;
using System.Security.Cryptography;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using NArk.Swaps.Evm;
using NArk.Swaps.Evm.Contracts;
using NArk.Swaps.Evm.Contracts.Test;

namespace NArk.Tests.End2End.Evm;

/// <summary>
/// Integration tests for <see cref="EvmChainClient"/> against a real (freshly deployed)
/// <c>ERC20Swap</c> contract on a local Anvil node — the EVM analogue of the Bitcoin-side
/// E2E tests elsewhere in this project, except EVM has its own disposable "regtest"
/// (Anvil) that has nothing to do with nigiri/arkd.
///
/// Test scenarios are ported from BoltzExchange/boltz-core's own
/// <c>contracts/test/ERC20SwapTest.sol</c> (Foundry tests for the contract itself) — that
/// project has already enumerated the edge cases that matter for this contract; we reuse the
/// same scenarios here to validate our own Nethereum bindings/client against them, restricted
/// to the non-cooperative subset this milestone actually implements (no
/// testClaimWithSignature*/testClaimBatch*/testRefundCooperative*/testCommitClaim* —
/// those are cooperative/batch/EIP-712, out of scope — see EvmSwapAction).
///
/// Requires: anvil running locally (see SharedEvmInfrastructure). Deploys a fresh
/// TestERC20 + ERC20Swap once per test run — each test uses its own random preimage/hash so
/// tests don't interfere with each other despite sharing one deployed ERC20Swap instance
/// (exception: allowance is shared state too — see Lock_WithoutApproval_Fails).
/// </summary>
[Category("Evm")]
public class Erc20SwapContractTests
{
    private Web3 _web3 = null!;
    private Account _deployer = null!;
    private string _tokenAddress = null!;
    private string _swapAddress = null!;

    [OneTimeSetUp]
    public async Task DeployContracts()
    {
        _deployer = new Account(SharedEvmInfrastructure.DeployerPrivateKey);
        _web3 = new Web3(_deployer, SharedEvmInfrastructure.AnvilRpcUrl);

        var tokenReceipt = await _web3.Eth.GetContractDeploymentHandler<TestERC20Deployment>()
            .SendRequestAndWaitForReceiptAsync(new TestERC20Deployment
            {
                Name = "Test tBTC",
                Symbol = "tTBTC",
                InitialDecimals = 8,
                InitialSupply = 1_000_000_000,
            });
        _tokenAddress = tokenReceipt.ContractAddress;

        var swapReceipt = await _web3.Eth.GetContractDeploymentHandler<ERC20SwapDeployment>()
            .SendRequestAndWaitForReceiptAsync(new ERC20SwapDeployment());
        _swapAddress = swapReceipt.ContractAddress;
    }

    private EvmChainClient MakeClient() => new(_web3, _swapAddress);

    private static byte[] RandomPreimage()
    {
        var bytes = new byte[32];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private async Task<BigInteger> FarFutureTimelock() =>
        (BigInteger)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value + 1000;

    /// <summary>
    /// Asserts a call reverts, tolerant of both ways Nethereum can surface it (a thrown
    /// exception from pre-flight gas estimation, or a mined transaction with Status == 0) —
    /// not verified live against a real Anvil node in this session, so being liberal here
    /// beats guessing wrong and asserting on behavior that doesn't actually occur.
    /// </summary>
    private static async Task AssertRevertsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch
        {
            return; // threw — that's a revert, test passes
        }

        Assert.Fail("Expected the call to revert, but it completed without throwing.");
    }

    // ── testLockup0ValueFail ─────────────────────────────────────────────────

    [Test]
    public async Task Lock_ZeroAmount_Fails()
    {
        var client = MakeClient();
        var preimageHash = SHA256.HashData(RandomPreimage());
        var timelock = await FarFutureTimelock();

        await AssertRevertsAsync(() =>
            client.LockAsync(preimageHash, BigInteger.Zero, _tokenAddress, _deployer.Address, timelock));
    }

    // ── testLockupNoApprovalFail ─────────────────────────────────────────────

    [Test]
    public async Task Lock_WithoutApproval_Fails()
    {
        var client = MakeClient();
        var preimageHash = SHA256.HashData(RandomPreimage());
        var timelock = await FarFutureTimelock();

        // All tests in this fixture share one deployed contract + deployer account (one
        // OneTimeSetUp, not per-test) — approve() *sets* the allowance, so leftover allowance
        // from an earlier test that approved more than it spent would otherwise let this
        // "no approval" scenario accidentally succeed depending on test execution order.
        // Explicitly zero it out first so this test means what it says regardless of history.
        await client.ApproveTokenAsync(_tokenAddress, 0);

        await AssertRevertsAsync(() =>
            client.LockAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock));
    }

    // ── testLockup ───────────────────────────────────────────────────────────

    [Test]
    public async Task Lock_HappyPath_EmitsLockupEvent()
    {
        var client = MakeClient();
        var preimageHash = SHA256.HashData(RandomPreimage());
        var timelock = await FarFutureTimelock();

        await client.ApproveTokenAsync(_tokenAddress, 1000);
        await client.LockAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock);

        var lockup = await client.FindLockupEventAsync(preimageHash);
        Assert.That(lockup, Is.Not.Null);
        Assert.That(lockup!.Amount, Is.EqualTo((BigInteger)1000));
        Assert.That(lockup.TokenAddress, Is.EqualTo(_tokenAddress).IgnoreCase);
    }

    // ── testLockWithSameHashValueFail ────────────────────────────────────────

    [Test]
    public async Task Lock_SameHashTwice_Fails()
    {
        var client = MakeClient();
        var preimageHash = SHA256.HashData(RandomPreimage());
        var timelock = await FarFutureTimelock();

        await client.ApproveTokenAsync(_tokenAddress, 2000);
        await client.LockAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock);

        await AssertRevertsAsync(() =>
            client.LockAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock));
    }

    // ── testClaimWithInvalidPreimageFail ─────────────────────────────────────

    [Test]
    public async Task Claim_WithInvalidPreimage_Fails()
    {
        var client = MakeClient();
        var preimage = RandomPreimage();
        var preimageHash = SHA256.HashData(preimage);
        var timelock = await FarFutureTimelock();

        await client.ApproveTokenAsync(_tokenAddress, 1000);
        await client.LockAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock);

        await AssertRevertsAsync(() =>
            client.ClaimAsync(RandomPreimage(), 1000, _tokenAddress, _deployer.Address, timelock));
    }

    // ── testClaim ────────────────────────────────────────────────────────────

    [Test]
    public async Task Claim_HappyPath_EmitsClaimEvent()
    {
        var client = MakeClient();
        var preimage = RandomPreimage();
        var preimageHash = SHA256.HashData(preimage);
        var timelock = await FarFutureTimelock();

        await client.ApproveTokenAsync(_tokenAddress, 1000);
        await client.LockAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock);
        await client.ClaimAsync(preimage, 1000, _tokenAddress, _deployer.Address, timelock);

        var claim = await client.FindClaimEventAsync(preimageHash);
        Assert.That(claim, Is.Not.Null);
        Assert.That(claim!.Preimage, Is.EqualTo(preimage));
    }

    // ── testClaimTwiceFail ───────────────────────────────────────────────────

    [Test]
    public async Task Claim_Twice_Fails()
    {
        var client = MakeClient();
        var preimage = RandomPreimage();
        var preimageHash = SHA256.HashData(preimage);
        var timelock = await FarFutureTimelock();

        await client.ApproveTokenAsync(_tokenAddress, 1000);
        await client.LockAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock);
        await client.ClaimAsync(preimage, 1000, _tokenAddress, _deployer.Address, timelock);

        await AssertRevertsAsync(() =>
            client.ClaimAsync(preimage, 1000, _tokenAddress, _deployer.Address, timelock));
    }

    // ── testRefundNotTimedOutFail ────────────────────────────────────────────

    [Test]
    public async Task Refund_BeforeTimelock_Fails()
    {
        var client = MakeClient();
        var preimageHash = SHA256.HashData(RandomPreimage());
        var timelock = await FarFutureTimelock(); // deliberately not yet reached

        await client.ApproveTokenAsync(_tokenAddress, 1000);
        await client.LockAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock);

        await AssertRevertsAsync(() =>
            client.RefundAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock));
    }

    // ── testRefund ───────────────────────────────────────────────────────────

    [Test]
    public async Task Refund_HappyPath_EmitsRefundEvent()
    {
        var client = MakeClient();
        var preimageHash = SHA256.HashData(RandomPreimage());
        // One block past current — the approve+lock transactions below each mine a block on
        // Anvil's default auto-mine, so by the time we call refund, this timelock has passed.
        var timelock = (BigInteger)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value + 1;

        await client.ApproveTokenAsync(_tokenAddress, 1000);
        await client.LockAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock);
        await client.RefundAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock);

        var refund = await client.FindRefundEventAsync(preimageHash);
        Assert.That(refund, Is.Not.Null);
    }

    // ── testRefundTwiceFail ──────────────────────────────────────────────────

    [Test]
    public async Task Refund_Twice_Fails()
    {
        var client = MakeClient();
        var preimageHash = SHA256.HashData(RandomPreimage());
        var timelock = (BigInteger)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value + 1;

        await client.ApproveTokenAsync(_tokenAddress, 1000);
        await client.LockAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock);
        await client.RefundAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock);

        await AssertRevertsAsync(() =>
            client.RefundAsync(preimageHash, 1000, _tokenAddress, _deployer.Address, timelock));
    }
}
