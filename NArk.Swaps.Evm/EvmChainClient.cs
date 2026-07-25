using System.Net.Http.Json;
using System.Numerics;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using NArk.Swaps.Evm.Contracts;
using NArk.Swaps.Evm.Contracts.Erc20;
using NArk.Swaps.Evm.Models;

namespace NArk.Swaps.Evm;

/// <summary>
/// Thin Nethereum wrapper around Boltz's <c>ERC20Swap</c> HTLC contract on an EVM chain
/// (Arbitrum for this milestone). Handles the non-cooperative path only: approve, lock,
/// claim (with preimage), refund (after timelock). No EIP-712 cooperative signing yet —
/// see the plan's deferred-scope section.
/// </summary>
public class EvmChainClient
{
    private readonly Web3 _web3;

    /// <summary>Address of the deployed <c>ERC20Swap</c> contract on the target chain.</summary>
    public string Erc20SwapAddress { get; }

    public EvmChainClient(Web3 web3, string erc20SwapAddress)
    {
        _web3 = web3;
        Erc20SwapAddress = erc20SwapAddress;
    }

    /// <summary>
    /// Resolves the <c>EtherSwap</c>/<c>ERC20Swap</c> contract addresses and chain id for a
    /// Boltz-supported EVM chain via <c>GET /v2/chain/{currency}/contracts</c>. The caller
    /// owns the <see cref="HttpClient"/> (base address pointed at the Boltz API).
    /// </summary>
    public static async Task<EvmChainContractsResponse> GetChainInfoAsync(
        HttpClient boltzHttpClient, string chain, CancellationToken ct = default)
    {
        var response = await boltzHttpClient.GetFromJsonAsync<EvmChainContractsResponse>(
            $"v2/chain/{chain}/contracts", ct);

        return response ?? throw new InvalidOperationException(
            $"Boltz returned no contract info for EVM chain '{chain}'.");
    }

    /// <summary>Approves the <c>ERC20Swap</c> contract to spend <paramref name="amount"/> of <paramref name="tokenAddress"/>.</summary>
    public async Task<TransactionReceipt> ApproveTokenAsync(
        string tokenAddress, BigInteger amount, CancellationToken ct = default)
    {
        var handler = _web3.Eth.GetContractHandler(tokenAddress);
        return await handler.SendRequestAndWaitForReceiptAsync(new ApproveFunction
        {
            Spender = Erc20SwapAddress,
            Value = amount,
        }, cancellationToken: ct);
    }

    /// <summary>Locks <paramref name="amount"/> of <paramref name="tokenAddress"/> in <c>ERC20Swap</c> for the given preimage hash.</summary>
    public async Task<TransactionReceipt> LockAsync(
        byte[] preimageHash, BigInteger amount, string tokenAddress, string claimAddress, BigInteger timelock,
        CancellationToken ct = default)
    {
        var handler = _web3.Eth.GetContractHandler(Erc20SwapAddress);
        return await handler.SendRequestAndWaitForReceiptAsync(new LockFunction
        {
            PreimageHash = preimageHash,
            Amount = amount,
            TokenAddress = tokenAddress,
            ClaimAddress = claimAddress,
            Timelock = timelock,
        }, cancellationToken: ct);
    }

    /// <summary>Claims tokens locked in <c>ERC20Swap</c> by revealing the preimage.</summary>
    public async Task<TransactionReceipt> ClaimAsync(
        byte[] preimage, BigInteger amount, string tokenAddress, string refundAddress, BigInteger timelock,
        CancellationToken ct = default)
    {
        var handler = _web3.Eth.GetContractHandler(Erc20SwapAddress);
        return await handler.SendRequestAndWaitForReceiptAsync(new ClaimFunction
        {
            Preimage = preimage,
            Amount = amount,
            TokenAddress = tokenAddress,
            RefundAddress = refundAddress,
            Timelock = timelock,
        }, cancellationToken: ct);
    }

    /// <summary>Refunds tokens locked in <c>ERC20Swap</c> once <paramref name="timelock"/> has passed.</summary>
    public async Task<TransactionReceipt> RefundAsync(
        byte[] preimageHash, BigInteger amount, string tokenAddress, string claimAddress, BigInteger timelock,
        CancellationToken ct = default)
    {
        var handler = _web3.Eth.GetContractHandler(Erc20SwapAddress);
        return await handler.SendRequestAndWaitForReceiptAsync(new RefundFunction
        {
            PreimageHash = preimageHash,
            Amount = amount,
            TokenAddress = tokenAddress,
            ClaimAddress = claimAddress,
            Timelock = timelock,
        }, cancellationToken: ct);
    }

    /// <summary>Finds the <c>Lockup</c> event for a preimage hash, if any lockup has landed yet.</summary>
    public async Task<LockupEventDTO?> FindLockupEventAsync(byte[] preimageHash, CancellationToken ct = default)
    {
        var eventHandler = _web3.Eth.GetEvent<LockupEventDTO>(Erc20SwapAddress);
        var filter = eventHandler.CreateFilterInput();
        var logs = await eventHandler.GetAllChangesAsync(filter);
        return logs
            .Select(l => l.Event)
            .FirstOrDefault(e => e.PreimageHash.SequenceEqual(preimageHash));
    }

    /// <summary>Finds the <c>Claim</c> event for a preimage hash, if the swap has been claimed yet.</summary>
    public async Task<ClaimEventDTO?> FindClaimEventAsync(byte[] preimageHash, CancellationToken ct = default)
    {
        var eventHandler = _web3.Eth.GetEvent<ClaimEventDTO>(Erc20SwapAddress);
        var filter = eventHandler.CreateFilterInput();
        var logs = await eventHandler.GetAllChangesAsync(filter);
        return logs
            .Select(l => l.Event)
            .FirstOrDefault(e => e.PreimageHash.SequenceEqual(preimageHash));
    }

    /// <summary>Finds the <c>Refund</c> event for a preimage hash, if the swap has been refunded yet.</summary>
    public async Task<RefundEventDTO?> FindRefundEventAsync(byte[] preimageHash, CancellationToken ct = default)
    {
        var eventHandler = _web3.Eth.GetEvent<RefundEventDTO>(Erc20SwapAddress);
        var filter = eventHandler.CreateFilterInput();
        var logs = await eventHandler.GetAllChangesAsync(filter);
        return logs
            .Select(l => l.Event)
            .FirstOrDefault(e => e.PreimageHash.SequenceEqual(preimageHash));
    }
}
