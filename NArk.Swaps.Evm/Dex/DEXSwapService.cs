using System.Numerics;
using Nethereum.Signer;
using NArk.Swaps.Evm.Contracts.Router;

namespace NArk.Swaps.Evm.Dex;

/// <summary>
/// Milestone 4's USDT/generic-ERC20 DEX-hop support: locks/claims an arbitrary ERC20 (instead of
/// tBTC directly) by routing through Boltz's <c>Router</c> contract, which atomically executes a
/// DEX swap (via <see cref="IDexQuoteProvider"/>) alongside the lock/claim. Boltz's own backend
/// never sees the DEX hop — it only ever watches for the same <c>ERC20Swap</c> Lock/Claim events
/// on the same tBTC token it already watches for the plain flow (see
/// <see cref="EvmChainSwapProvider"/>), so nothing about the swap's lifecycle/status tracking
/// changes; only how the tBTC leg gets funded/spent does.
/// </summary>
// TODO: not wired into EvmChainSwapProvider/SwapsManagementServiceEvmExtensions yet — this class
// implements the atomic Router mechanics (verified live in RouterDexHopTests.cs) but nothing
// calls it from the actual swap-creation/claim/refund flow yet. Also depends on a real
// IDexQuoteProvider implementation, which doesn't exist yet either (see that interface's TODO).
public class DEXSwapService(RouterClient routerClient, IDexQuoteProvider dexQuoteProvider)
{
    /// <summary>
    /// EvmToArk lock, funded from an arbitrary ERC20 instead of tBTC directly: signs a Permit2
    /// witness transfer authorizing the Router to pull <paramref name="amountIn"/> of
    /// <paramref name="tokenIn"/> from <paramref name="ownerKey"/>'s account, swap it to
    /// <paramref name="lockedTokenAddress"/> (tBTC), and lock the result for Boltz to claim.
    /// </summary>
    /// <remarks>
    /// <paramref name="ownerKey"/> must have already granted Permit2 a one-time on-chain
    /// <c>approve(permit2Address, amountIn)</c> for <paramref name="tokenIn"/> — Permit2 moves
    /// funds via its own allowance even though no per-lock approval to the Router is needed. See
    /// <see cref="Permit2Signer"/>'s doc comment.
    /// </remarks>
    public async Task LockViaDexHopAsync(
        EthECKey ownerKey, string tokenIn, BigInteger amountIn, string lockedTokenAddress,
        byte[] preimageHash, string claimAddress, string refundAddress, BigInteger timelock,
        BigInteger permit2Nonce, BigInteger permit2Deadline, CancellationToken ct = default)
    {
        var quote = await dexQuoteProvider.GetSwapCallsAsync(tokenIn, lockedTokenAddress, amountIn, ct);
        var calls = quote.Calls.ToList();
        var callsHash = Permit2Signer.ComputeCallsHash(calls);

        var typehash = await routerClient.GetTypehashExecuteLockErc20Async(ct);
        var witness = Permit2Signer.ComputeWitness(
            typehash, preimageHash, lockedTokenAddress, claimAddress, refundAddress, timelock, callsHash);

        var permit2Address = await routerClient.GetPermit2AddressAsync(ct);
        var permit2DomainSeparator = await routerClient.GetDomainSeparatorAsync(permit2Address, ct);
        var typestring = await routerClient.GetTypestringExecuteLockErc20Async(ct);
        var owner = ownerKey.GetPublicAddress();
        var signature = Permit2Signer.Sign(
            ownerKey, permit2DomainSeparator, typestring, tokenIn, amountIn, routerClient.RouterAddress,
            permit2Nonce, permit2Deadline, witness);

        var permit = new PermitTransferFrom
        {
            Permitted = new TokenPermissions { Token = tokenIn, Amount = amountIn },
            Nonce = permit2Nonce,
            Deadline = permit2Deadline,
        };

        await routerClient.ExecuteAndLockErc20WithPermit2Async(
            preimageHash, lockedTokenAddress, claimAddress, refundAddress, timelock, calls, permit, owner, signature, ct);
    }

    /// <summary>
    /// ArkToEvm claim, swapped to an arbitrary ERC20 instead of kept as tBTC: atomically claims
    /// Boltz's <paramref name="lockedTokenAddress"/> (tBTC) lockup, swaps the claimed amount to
    /// <paramref name="outputToken"/>, and sweeps the proceeds to <paramref name="claimKey"/>'s
    /// account — all in one transaction, which <paramref name="claimKey"/>'s account must itself
    /// send (Router's msg.sender-gated claim overload — see <see cref="Erc20SwapClaimSigner"/>'s
    /// doc comment). Returns the actual amount swept.
    /// </summary>
    public async Task<BigInteger> ClaimAndSwapAsync(
        EthECKey claimKey, byte[] preimage, BigInteger amount, string lockedTokenAddress,
        string refundAddress, BigInteger timelock, string outputToken, CancellationToken ct = default)
    {
        var quote = await dexQuoteProvider.GetSwapCallsAsync(lockedTokenAddress, outputToken, amount, ct);
        var calls = quote.Calls.ToList();

        var erc20SwapAddress = await routerClient.GetErc20SwapAddressAsync(ct);
        var erc20SwapDomainSeparator = await routerClient.GetDomainSeparatorAsync(erc20SwapAddress, ct);
        var typehashClaim = await routerClient.GetErc20SwapTypehashClaimAsync(erc20SwapAddress, ct);

        // destination = the Router's own address: ERC20Swap sees the Router as msg.sender when
        // it calls ERC20Swap.claim on our behalf — see Erc20SwapClaimSigner's doc comment.
        var (r, s, v) = Erc20SwapClaimSigner.Sign(
            claimKey, erc20SwapDomainSeparator, typehashClaim, preimage, amount, lockedTokenAddress,
            refundAddress, timelock, routerClient.RouterAddress);

        var claim = new Erc20Claim
        {
            Preimage = preimage,
            Amount = amount,
            TokenAddress = lockedTokenAddress,
            RefundAddress = refundAddress,
            Timelock = timelock,
            V = v,
            R = r,
            S = s,
        };

        await routerClient.ClaimErc20ExecuteAsync(claim, calls, outputToken, quote.MinAmountOut, ct);
        return quote.MinAmountOut;
    }
}
