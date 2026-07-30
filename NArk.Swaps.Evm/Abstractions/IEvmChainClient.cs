using System.Numerics;
using Nethereum.RPC.Eth.DTOs;
using NArk.Swaps.Evm.Contracts;

namespace NArk.Swaps.Evm;

/// <summary>
/// The <c>ERC20Swap</c> operations <see cref="EvmChainSwapProvider"/> depends on: the HTLC
/// lock/claim/refund calls plus the event lookups its idempotency guards consult.
/// Implemented by <see cref="EvmChainClient"/> over Nethereum.
/// </summary>
/// <remarks>
/// Extracted so the provider's lock/claim/refund paths can be exercised without an RPC endpoint.
/// The decision logic those paths follow lives in <see cref="EvmIdempotencyResolver"/> and is
/// testable on its own; this interface is what a test needs in order to also cover the wiring —
/// that the transaction hash is recorded <em>between</em> the broadcast and the receipt wait,
/// which is the ordering the whole guard depends on.
/// </remarks>
public interface IEvmChainClient
{
    /// <summary>Address of the deployed <c>ERC20Swap</c> contract on the target chain.</summary>
    string Erc20SwapAddress { get; }

    /// <summary>Approves the <c>ERC20Swap</c> contract to spend <paramref name="amount"/> of <paramref name="tokenAddress"/>.</summary>
    Task<TransactionReceipt> ApproveTokenAsync(string tokenAddress, BigInteger amount, CancellationToken ct = default);

    /// <summary>Broadcasts <c>ERC20Swap.lock</c>, returning the transaction hash without waiting for the receipt.</summary>
    Task<string> SendLockAsync(
        byte[] preimageHash, BigInteger amount, string tokenAddress, string claimAddress, BigInteger timelock,
        CancellationToken ct = default);

    /// <summary>Broadcasts <c>ERC20Swap.claim</c>, returning the transaction hash without waiting for the receipt.</summary>
    Task<string> SendClaimAsync(
        byte[] preimage, BigInteger amount, string tokenAddress, string refundAddress, BigInteger timelock,
        CancellationToken ct = default);

    /// <summary>Broadcasts <c>ERC20Swap.refund</c>, returning the transaction hash without waiting for the receipt.</summary>
    Task<string> SendRefundAsync(
        byte[] preimageHash, BigInteger amount, string tokenAddress, string claimAddress, BigInteger timelock,
        CancellationToken ct = default);

    /// <summary>Polls for a transaction's receipt until it lands or the timeout elapses.</summary>
    Task<TransactionReceipt> WaitForReceiptAsync(
        string txHash, CancellationToken ct = default, TimeSpan? timeout = null, TimeSpan? pollInterval = null);

    /// <summary>Current block number on the target chain — used to convert a contract's absolute
    /// timeout block into a wall-clock deadline for timelock validation.</summary>
    Task<BigInteger> GetBlockNumberAsync(CancellationToken ct = default);

    /// <summary>Finds the <c>Lockup</c> event for a preimage hash, if any lockup has landed yet.</summary>
    Task<LockupEventDTO?> FindLockupEventAsync(byte[] preimageHash, CancellationToken ct = default);

    /// <summary>Finds the <c>Claim</c> event for a preimage hash, if the swap has been claimed yet.</summary>
    Task<ClaimEventDTO?> FindClaimEventAsync(byte[] preimageHash, CancellationToken ct = default);

    /// <summary>Finds the <c>Refund</c> event for a preimage hash, if the swap has been refunded yet.</summary>
    Task<RefundEventDTO?> FindRefundEventAsync(byte[] preimageHash, CancellationToken ct = default);
}
