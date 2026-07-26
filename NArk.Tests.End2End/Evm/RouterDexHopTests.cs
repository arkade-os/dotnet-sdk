using System.Numerics;
using System.Security.Cryptography;
using Nethereum.Contracts;
using Nethereum.Contracts.MessageEncodingServices;
using Nethereum.Signer;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using NArk.Swaps.Evm;
using NArk.Swaps.Evm.Contracts;
using NArk.Swaps.Evm.Contracts.Erc20;
using NArk.Swaps.Evm.Contracts.Permit2;
using NArk.Swaps.Evm.Contracts.Router;
using NArk.Swaps.Evm.Contracts.TestFixtures;
using TestERC20Deployment = NArk.Swaps.Evm.Contracts.Test.TestERC20Deployment;
using TypehashClaimFunction = NArk.Swaps.Evm.Contracts.TypehashClaimFunction;
using TypehashClaimOutputDTO = NArk.Swaps.Evm.Contracts.TypehashClaimOutputDTO;
using DomainSeparatorFunction = NArk.Swaps.Evm.Contracts.DomainSeparatorFunction;
using DomainSeparatorOutputDTO = NArk.Swaps.Evm.Contracts.DomainSeparatorOutputDTO;

namespace NArk.Tests.End2End.Evm;

/// <summary>
/// Integration tests for Milestone 4's USDT/generic-ERC20 DEX-hop path — proves
/// <see cref="Permit2Signer"/> and <see cref="Erc20SwapClaimSigner"/> produce signatures a real
/// deployed <c>Router</c> + real <c>Permit2</c> actually accept, since a wrong
/// typehash/field-order/padding fails safe (Permit2/ERC20Swap simply reject the signature) but
/// is otherwise impossible to catch by unit-testing the byte math in isolation — see those
/// classes' doc comments.
///
/// Deploys fresh to a local Anvil per <see cref="Erc20SwapContractTests"/>'s pattern, except
/// Permit2 is deployed by injecting its real canonical runtime bytecode directly via Anvil's
/// <c>anvil_setCode</c> (mirroring Uniswap's own <c>DeployPermit2.sol</c> test helper, which does
/// the equivalent via Foundry's <c>vm.etch</c>) rather than compiling it from source — see
/// <c>generate-bindings.sh</c>'s Permit2Deployment.cs extraction step.
///
/// The DEX hop itself uses <see cref="MockERC20Dex"/> — vendored verbatim from
/// BoltzExchange/boltz-core's own <c>Router.sol</c> Foundry test suite (see that file's header),
/// the same test double Boltz's own tests use for this exact purpose. Production code targets a
/// real DEX (Uniswap V3) instead; only the Permit2/Router calldata plumbing — the fund-critical
/// part — is what these tests actually need to prove correct.
/// </summary>
[Category("Evm")]
public class RouterDexHopTests
{
    private Web3 _web3 = null!;
    private Account _deployer = null!;
    private EthECKey _deployerKey = null!;
    private string _erc20SwapAddress = null!;
    private string _routerAddress = null!;
    private string _permit2Address = null!;
    private string _usdtAddress = null!;
    private string _tbtcAddress = null!;
    private string _dexUsdtToTbtcAddress = null!;
    private string _dexTbtcToUsdtAddress = null!;

    private byte[] _permit2DomainSeparator = null!;
    private byte[] _routerTypehashExecuteLockErc20 = null!;
    private string _routerTypestringExecuteLockErc20 = null!;
    private byte[] _erc20SwapDomainSeparator = null!;
    private byte[] _erc20SwapTypehashClaim = null!;

    [OneTimeSetUp]
    public async Task DeployContracts()
    {
        _deployerKey = new EthECKey(SharedEvmInfrastructure.DeployerPrivateKey);
        _deployer = new Account(SharedEvmInfrastructure.DeployerPrivateKey);
        _web3 = new Web3(_deployer, SharedEvmInfrastructure.AnvilRpcUrl);

        // Real Permit2 runtime bytecode, injected at its real canonical address — see this
        // class's doc comment.
        _permit2Address = Permit2Deployment.CanonicalAddress;
        await _web3.Client.SendRequestAsync<string>(
            "anvil_setCode", null, _permit2Address, Permit2Deployment.RuntimeBytecodeHex);

        var erc20SwapReceipt = await _web3.Eth.GetContractDeploymentHandler<ERC20SwapDeployment>()
            .SendRequestAndWaitForReceiptAsync(new ERC20SwapDeployment());
        _erc20SwapAddress = erc20SwapReceipt.ContractAddress;

        // Router's constructor also requires an EtherSwap-shaped address, but nothing in this
        // fixture's ERC20-only flows (executeAndLockERC20WithPermit2/claimERC20Execute) ever
        // touches SWAP_CONTRACT (see Router.sol) — any deployed address is structurally valid.
        var routerReceipt = await _web3.Eth.GetContractDeploymentHandler<RouterDeployment>()
            .SendRequestAndWaitForReceiptAsync(new RouterDeployment
            {
                SwapContract = _erc20SwapAddress,
                Erc20SwapContract = _erc20SwapAddress,
                Permit2Contract = _permit2Address,
            });
        _routerAddress = routerReceipt.ContractAddress;

        var usdtReceipt = await _web3.Eth.GetContractDeploymentHandler<TestERC20Deployment>()
            .SendRequestAndWaitForReceiptAsync(new TestERC20Deployment
            {
                Name = "Test USDT", Symbol = "tUSDT", InitialDecimals = 6, InitialSupply = 1_000_000_000_000,
            });
        _usdtAddress = usdtReceipt.ContractAddress;

        var tbtcReceipt = await _web3.Eth.GetContractDeploymentHandler<TestERC20Deployment>()
            .SendRequestAndWaitForReceiptAsync(new TestERC20Deployment
            {
                Name = "Test tBTC", Symbol = "tTBTC", InitialDecimals = 8, InitialSupply = 1_000_000_000_000,
            });
        _tbtcAddress = tbtcReceipt.ContractAddress;

        var dex1Receipt = await _web3.Eth.GetContractDeploymentHandler<MockERC20DexDeployment>()
            .SendRequestAndWaitForReceiptAsync(new MockERC20DexDeployment
            { InputToken = _usdtAddress, OutputToken = _tbtcAddress });
        _dexUsdtToTbtcAddress = dex1Receipt.ContractAddress;

        var dex2Receipt = await _web3.Eth.GetContractDeploymentHandler<MockERC20DexDeployment>()
            .SendRequestAndWaitForReceiptAsync(new MockERC20DexDeployment
            { InputToken = _tbtcAddress, OutputToken = _usdtAddress });
        _dexTbtcToUsdtAddress = dex2Receipt.ContractAddress;

        // Fund each mock DEX with the token it needs to pay out (MockERC20Dex.swap() is a plain
        // 1:1 transferFrom/transfer, not an AMM pool — see that vendored contract's own body).
        await TransferAsync(_tbtcAddress, _dexUsdtToTbtcAddress, 1_000_000_000);
        await TransferAsync(_usdtAddress, _dexTbtcToUsdtAddress, 1_000_000_000);

        _permit2DomainSeparator = await QueryDomainSeparatorAsync(_permit2Address);
        _erc20SwapDomainSeparator = await QueryDomainSeparatorAsync(_erc20SwapAddress);

        var routerHandler = _web3.Eth.GetContractHandler(_routerAddress);
        _routerTypehashExecuteLockErc20 = (await routerHandler
            .QueryDeserializingToObjectAsync<TypehashExecuteLockErc20Function, TypehashExecuteLockErc20OutputDTO>())
            .ReturnValue1;
        _routerTypestringExecuteLockErc20 = (await routerHandler
            .QueryDeserializingToObjectAsync<TypestringExecuteLockErc20Function, TypestringExecuteLockErc20OutputDTO>())
            .ReturnValue1;

        var erc20SwapHandler = _web3.Eth.GetContractHandler(_erc20SwapAddress);
        _erc20SwapTypehashClaim = (await erc20SwapHandler
            .QueryDeserializingToObjectAsync<TypehashClaimFunction, TypehashClaimOutputDTO>())
            .ReturnValue1;
    }

    // DOMAIN_SEPARATOR() has the identical selector/signature on Permit2, ERC20Swap, and
    // Router — the C# binding type is interchangeable across all three deployed addresses.
    private async Task<byte[]> QueryDomainSeparatorAsync(string contractAddress) =>
        (await _web3.Eth.GetContractHandler(contractAddress)
            .QueryDeserializingToObjectAsync<DomainSeparatorFunction, DomainSeparatorOutputDTO>())
            .ReturnValue1;

    private async Task TransferEtherAsync(string to, BigInteger weiAmount) =>
        await _web3.Eth.GetEtherTransferService().TransferEtherAndWaitForReceiptAsync(to, Web3.Convert.FromWei(weiAmount));

    private async Task TransferAsync(string tokenAddress, string to, BigInteger amount) =>
        await _web3.Eth.GetContractHandler(tokenAddress)
            .SendRequestAndWaitForReceiptAsync(new TransferFunction { To = to, Value = amount });

    private async Task<BigInteger> BalanceOfAsync(string tokenAddress, string owner) =>
        await _web3.Eth.GetContractHandler(tokenAddress)
            .QueryAsync<BalanceOfFunction, BigInteger>(new BalanceOfFunction { Account = owner });

    private static byte[] EncodeCall<T>(T function) where T : FunctionMessage =>
        new FunctionMessageEncodingService<T>().GetCallData(function);

    private static byte[] RandomPreimage()
    {
        var bytes = new byte[32];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private async Task<BigInteger> FarFutureTimelock() =>
        (BigInteger)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value + 1000;

    [Test]
    public async Task ExecuteAndLockERC20WithPermit2_SwapsUsdtToTbtcThenLocks()
    {
        const long amount = 5_000_000;
        var preimageHash = SHA256.HashData(RandomPreimage());
        var timelock = await FarFutureTimelock();
        var claimAddress = EthECKey.GenerateKey().GetPublicAddress();
        var refundAddress = _deployer.Address;

        var calls = new List<Call>
        {
            new() { Target = _usdtAddress, Value = 0, CallData = EncodeCall(new ApproveFunction { Spender = _dexUsdtToTbtcAddress, Value = amount }) },
            new() { Target = _dexUsdtToTbtcAddress, Value = 0, CallData = EncodeCall(new SwapFunction { Amount = amount }) },
        };
        var callsHash = Permit2Signer.ComputeCallsHash(calls);
        var witness = Permit2Signer.ComputeWitness(
            _routerTypehashExecuteLockErc20, preimageHash, _tbtcAddress, claimAddress, refundAddress, timelock, callsHash);

        // One-time on-chain approve to Permit2 itself — still required even with witness
        // signatures (Permit2 moves funds via its own allowance). See Permit2Signer's doc comment.
        await _web3.Eth.GetContractHandler(_usdtAddress)
            .SendRequestAndWaitForReceiptAsync(new ApproveFunction { Spender = _permit2Address, Value = amount });

        var permit = new PermitTransferFrom
        {
            Permitted = new TokenPermissions { Token = _usdtAddress, Amount = amount },
            // Random rather than a fixed literal: Permit2's nonce bitmap lives in its own contract
            // storage at a fixed, persistent address (this test's anvil_setCode deploy doesn't
            // reset it), so a hardcoded nonce would only work on the very first run against a
            // given Anvil instance and fail every run after with an already-invalidated nonce.
            Nonce = new BigInteger(Random.Shared.NextInt64(0, long.MaxValue)),
            Deadline = int.MaxValue,
        };
        var signature = Permit2Signer.Sign(
            _deployerKey, _permit2DomainSeparator, _routerTypestringExecuteLockErc20, _usdtAddress, amount,
            _routerAddress, permit.Nonce, permit.Deadline, witness);

        var usdtBefore = await BalanceOfAsync(_usdtAddress, _deployer.Address);

        await _web3.Eth.GetContractHandler(_routerAddress).SendRequestAndWaitForReceiptAsync(
            new ExecuteAndLockERC20WithPermit2Function
            {
                PreimageHash = preimageHash,
                TokenAddress = _tbtcAddress,
                ClaimAddress = claimAddress,
                RefundAddress = refundAddress,
                Timelock = timelock,
                Calls = calls,
                Permit = permit,
                Owner = _deployer.Address,
                Signature = signature,
            });

        var usdtAfter = await BalanceOfAsync(_usdtAddress, _deployer.Address);
        Assert.That(usdtBefore - usdtAfter, Is.EqualTo((BigInteger)amount),
            "USDT should have been pulled via Permit2 and fully swapped away");

        var erc20SwapHandler = _web3.Eth.GetContractHandler(_erc20SwapAddress);
        var hash = await erc20SwapHandler.QueryAsync<HashValuesFunction, byte[]>(new HashValuesFunction
        {
            PreimageHash = preimageHash, Amount = amount, TokenAddress = _tbtcAddress,
            ClaimAddress = claimAddress, RefundAddress = refundAddress, Timelock = timelock,
        });
        var locked = await erc20SwapHandler.QueryAsync<SwapsFunction, bool>(new SwapsFunction { ReturnValue1 = hash });
        Assert.That(locked, Is.True, "expected tBTC to have been locked in ERC20Swap after the DEX hop");

        Assert.That(await BalanceOfAsync(_tbtcAddress, _routerAddress), Is.EqualTo(BigInteger.Zero), "Router should hold no leftover tBTC");
        Assert.That(await BalanceOfAsync(_usdtAddress, _routerAddress), Is.EqualTo(BigInteger.Zero), "Router should hold no leftover USDT");
    }

    [Test]
    public async Task ClaimERC20Execute_ClaimsTbtcThenSwapsToUsdt()
    {
        const long amount = 3_000_000;
        var preimage = RandomPreimage();
        var preimageHash = SHA256.HashData(preimage);
        var timelock = await FarFutureTimelock();
        var claimKey = EthECKey.GenerateKey();
        var claimAddress = claimKey.GetPublicAddress();
        var refundAddress = _deployer.Address;

        // Lock tBTC into ERC20Swap directly (the ordinary path, already covered by
        // Erc20SwapContractTests) — this test is specifically about the claim+swap side.
        var evmChainClient = new EvmChainClient(_web3, _erc20SwapAddress);
        await evmChainClient.ApproveTokenAsync(_tbtcAddress, amount);
        await evmChainClient.LockAsync(preimageHash, amount, _tbtcAddress, claimAddress, timelock);

        // Router.claimERC20Execute's 4-arg overload requires msg.sender (the account calling
        // Router directly) to equal the recovered claim address — matching our real production
        // shape exactly, since EvmChainSwapProvider is always both the claim address Boltz
        // locked funds for and the account broadcasting the claim transaction.
        await TransferEtherAsync(claimAddress, 1_000_000_000_000_000_000);
        var claimerWeb3 = new Web3(new Account(claimKey.GetPrivateKey()), SharedEvmInfrastructure.AnvilRpcUrl);

        var calls = new List<Call>
        {
            new() { Target = _tbtcAddress, Value = 0, CallData = EncodeCall(new ApproveFunction { Spender = _dexTbtcToUsdtAddress, Value = amount }) },
            new() { Target = _dexTbtcToUsdtAddress, Value = 0, CallData = EncodeCall(new SwapFunction { Amount = amount }) },
        };

        // destination = the Router's own address: ERC20Swap sees the Router as msg.sender when
        // it calls ERC20Swap.claim on our behalf — see Erc20SwapClaimSigner's doc comment.
        var (r, s, v) = Erc20SwapClaimSigner.Sign(
            claimKey, _erc20SwapDomainSeparator, _erc20SwapTypehashClaim,
            preimage, amount, _tbtcAddress, refundAddress, timelock, _routerAddress);

        var claim = new Erc20Claim
        {
            Preimage = preimage, Amount = amount, TokenAddress = _tbtcAddress,
            RefundAddress = refundAddress, Timelock = timelock, V = v, R = r, S = s,
        };

        var usdtBefore = await BalanceOfAsync(_usdtAddress, claimAddress);

        await claimerWeb3.Eth.GetContractHandler(_routerAddress).SendRequestAndWaitForReceiptAsync(
            new ClaimERC20ExecuteFunction { Claim = claim, Calls = calls, Token = _usdtAddress, MinAmountOut = amount });

        var usdtAfter = await BalanceOfAsync(_usdtAddress, claimAddress);
        Assert.That(usdtAfter - usdtBefore, Is.EqualTo((BigInteger)amount),
            "expected the claimed tBTC to have been swapped and swept to us as USDT");

        Assert.That(await BalanceOfAsync(_tbtcAddress, _routerAddress), Is.EqualTo(BigInteger.Zero), "Router should hold no leftover tBTC");
        Assert.That(await BalanceOfAsync(_usdtAddress, _routerAddress), Is.EqualTo(BigInteger.Zero), "Router should hold no leftover USDT");
    }
}
