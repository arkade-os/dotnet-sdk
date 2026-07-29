using System.Numerics;
using Nethereum.Web3;
using NArk.Swaps.Evm.Contracts.Router;
using NArk.Swaps.Evm.Dex;

namespace NArk.Swaps.Evm;

/// <summary>
/// Thin Nethereum wrapper around Boltz's <c>Router</c> contract — the DEX-hop entry points
/// (<c>executeAndLockERC20WithPermit2</c>, <c>claimERC20Execute</c>) that let
/// <see cref="DEXSwapService"/> lock/claim an arbitrary ERC20 (e.g. USDT) instead of tBTC
/// directly. Mirrors <see cref="EvmChainClient"/>'s shape for the plain <c>ERC20Swap</c> contract.
///
/// Every address/typehash/typestring/domain-separator this class needs is queried live from the
/// deployed contracts rather than configured/hardcoded — Router, Permit2, and ERC20Swap all
/// expose their own constants as public view functions, so there is exactly one source of truth
/// (the chain itself) instead of config that could drift from what's actually deployed.
/// </summary>
public class RouterClient
{
    private readonly Web3 _web3;
    private readonly EvmNonceGuard _nonceGuard;

    /// <summary>Address of the deployed <c>Router</c> contract.</summary>
    public string RouterAddress { get; }

    /// <param name="web3">Web3 instance carrying the signing account.</param>
    /// <param name="routerAddress">Address of the deployed <c>Router</c> contract.</param>
    /// <param name="nonceGuard">
    /// Serialises broadcasts sharing this account's nonce. The DEX-hop path signs Router calls
    /// with the same key <see cref="EvmChainClient"/> uses for plain lock/claim/refund, so both
    /// must be given the <em>same</em> guard instance — separate guards leave the two clients
    /// free to collide on a nonce. Defaults to a private guard.
    /// </param>
    public RouterClient(Web3 web3, string routerAddress, EvmNonceGuard? nonceGuard = null)
    {
        _web3 = web3;
        RouterAddress = routerAddress;
        _nonceGuard = nonceGuard ?? new EvmNonceGuard();
    }

    public async Task<string> GetPermit2AddressAsync(CancellationToken ct = default) =>
        (await _web3.Eth.GetContractHandler(RouterAddress)
            .QueryDeserializingToObjectAsync<Permit2Function, Permit2OutputDTO>()).ReturnValue1;

    public async Task<string> GetErc20SwapAddressAsync(CancellationToken ct = default) =>
        (await _web3.Eth.GetContractHandler(RouterAddress)
            .QueryDeserializingToObjectAsync<Erc20SwapContractFunction, Erc20SwapContractOutputDTO>()).ReturnValue1;

    public async Task<byte[]> GetDomainSeparatorAsync(string contractAddress, CancellationToken ct = default) =>
        (await _web3.Eth.GetContractHandler(contractAddress)
            .QueryDeserializingToObjectAsync<DomainSeparatorFunction, DomainSeparatorOutputDTO>()).ReturnValue1;

    public async Task<byte[]> GetTypehashExecuteLockErc20Async(CancellationToken ct = default) =>
        (await _web3.Eth.GetContractHandler(RouterAddress)
            .QueryDeserializingToObjectAsync<TypehashExecuteLockErc20Function, TypehashExecuteLockErc20OutputDTO>()).ReturnValue1;

    public async Task<string> GetTypestringExecuteLockErc20Async(CancellationToken ct = default) =>
        (await _web3.Eth.GetContractHandler(RouterAddress)
            .QueryDeserializingToObjectAsync<TypestringExecuteLockErc20Function, TypestringExecuteLockErc20OutputDTO>()).ReturnValue1;

    /// <summary>
    /// ERC20Swap's own <c>TYPEHASH_CLAIM</c> (not Router's constant of the same name, which is
    /// for the native-ETH <c>Claim</c> struct) — needed for <see cref="Erc20SwapClaimSigner"/>.
    /// Fully-qualified rather than a plain type reference: Router.sol declares its own
    /// <c>TYPEHASH_CLAIM</c> too, so the bare generated type name is ambiguous between the two
    /// namespaces.
    /// </summary>
    public async Task<byte[]> GetErc20SwapTypehashClaimAsync(string erc20SwapAddress, CancellationToken ct = default) =>
        (await _web3.Eth.GetContractHandler(erc20SwapAddress)
            .QueryDeserializingToObjectAsync<NArk.Swaps.Evm.Contracts.TypehashClaimFunction, NArk.Swaps.Evm.Contracts.TypehashClaimOutputDTO>())
            .ReturnValue1;

    /// <summary>
    /// Atomically pulls <paramref name="permit"/>'s token via Permit2 (no prior on-chain approve
    /// to the Router itself needed — see <see cref="Permit2Signer"/>'s doc comment), executes
    /// <paramref name="calls"/> (the DEX hop), then locks the resulting
    /// <paramref name="tokenAddress"/> balance into <c>ERC20Swap</c>.
    /// </summary>
    public async Task ExecuteAndLockErc20WithPermit2Async(
        byte[] preimageHash, string tokenAddress, string claimAddress, string refundAddress, BigInteger timelock,
        List<Call> calls, PermitTransferFrom permit, string owner, byte[] signature, CancellationToken ct = default)
    {
        var txHash = await _nonceGuard.BroadcastAsync(
            () => _web3.Eth.GetContractHandler(RouterAddress).SendRequestAsync(
            new ExecuteAndLockERC20WithPermit2Function
            {
                PreimageHash = preimageHash,
                TokenAddress = tokenAddress,
                ClaimAddress = claimAddress,
                RefundAddress = refundAddress,
                Timelock = timelock,
                Calls = calls,
                Permit = permit,
                Owner = owner,
                Signature = signature,
            }), ct);
        await EvmReceipts.WaitAsync(_web3, txHash, ct, null, null);
    }

    /// <summary>
    /// Claims an <c>ERC20Swap</c> lockup on the caller's behalf (via <paramref name="claim"/>'s
    /// cooperative-claim signature — see <see cref="Erc20SwapClaimSigner"/>'s doc comment),
    /// executes <paramref name="calls"/> (the DEX hop), then sweeps the resulting
    /// <paramref name="token"/> balance to the caller. Must be called by the same account whose
    /// key signed <paramref name="claim"/> — Router's msg.sender-gated overload, no separate
    /// EIP-712 authorization for the execute+sweep step itself.
    /// </summary>
    public async Task ClaimErc20ExecuteAsync(
        Erc20Claim claim, List<Call> calls, string token, BigInteger minAmountOut, CancellationToken ct = default)
    {
        var txHash = await _nonceGuard.BroadcastAsync(
            () => _web3.Eth.GetContractHandler(RouterAddress).SendRequestAsync(
                new ClaimERC20ExecuteFunction { Claim = claim, Calls = calls, Token = token, MinAmountOut = minAmountOut }),
            ct);
        await EvmReceipts.WaitAsync(_web3, txHash, ct, null, null);
    }
}
