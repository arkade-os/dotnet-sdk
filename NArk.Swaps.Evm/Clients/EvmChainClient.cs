using System.Numerics;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using NArk.Swaps.Boltz.Client;
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
// Nonce safety: every broadcast goes through EvmNonceGuard, which serialises nonce resolution,
// signing and eth_sendRawTransaction for the account. Receipt waiting deliberately stays outside
// that gate — see EvmNonceGuard's doc comment. Clients sharing an account (notably RouterClient
// on the DEX-hop path) must be handed the same guard instance.
public class EvmChainClient : IEvmChainClient
{
    private readonly Web3 _web3;
    private readonly EvmNonceGuard _nonceGuard;

    /// <summary>Address of the deployed <c>ERC20Swap</c> contract on the target chain.</summary>
    public string Erc20SwapAddress { get; }

    /// <param name="web3">Web3 instance carrying the signing account.</param>
    /// <param name="erc20SwapAddress">Address of the deployed <c>ERC20Swap</c> contract.</param>
    /// <param name="nonceGuard">
    /// Serialises broadcasts sharing this account's nonce. Pass the same instance to every client
    /// signing with the same key — notably <see cref="RouterClient"/> on the DEX-hop path.
    /// Defaults to a private guard, which is correct only when this client is the sole sender.
    /// </param>
    public EvmChainClient(Web3 web3, string erc20SwapAddress, EvmNonceGuard? nonceGuard = null)
    {
        _web3 = web3;
        Erc20SwapAddress = erc20SwapAddress;
        _nonceGuard = nonceGuard ?? new EvmNonceGuard();
    }

    /// <summary>
    /// Resolves the <c>EtherSwap</c>/<c>ERC20Swap</c> contract addresses and chain id for a
    /// Boltz-supported EVM chain via <c>GET /v2/chain/{currency}/contracts</c>. Reuses the
    /// caller's <see cref="BoltzClient"/> rather than a separate <see cref="HttpClient"/> to
    /// the same backend.
    /// </summary>
    public static async Task<EvmChainContractsResponse> GetChainInfoAsync(
        BoltzClient boltzClient, string chain, CancellationToken ct = default)
    {
        var response = await boltzClient.GetFromJsonAsync<EvmChainContractsResponse>(
            $"v2/chain/{chain}/contracts", ct);

        return response ?? throw new InvalidOperationException(
            $"Boltz returned no contract info for EVM chain '{chain}'.");
    }

    // TODO: approves exactly `amount` on every call rather than checking existing allowance /
    // approving once for a large/infinite amount — an extra on-chain approve tx (and its gas
    // cost) per swap. Fine for correctness, worth revisiting for gas efficiency once this is
    // more than a demo.
    /// <summary>Approves the <c>ERC20Swap</c> contract to spend <paramref name="amount"/> of <paramref name="tokenAddress"/>.</summary>
    public async Task<TransactionReceipt> ApproveTokenAsync(
        string tokenAddress, BigInteger amount, CancellationToken ct = default)
    {
        var txHash = await _nonceGuard.BroadcastAsync(
            () => _web3.Eth.GetContractHandler(tokenAddress).SendRequestAsync(new ApproveFunction
            {
                Spender = Erc20SwapAddress,
                Value = amount,
            }), ct);
        return await WaitForReceiptAsync(txHash, ct);
    }

    // ── Broadcast / receipt split ────────────────────────────────────────────
    // Each state-changing call comes in two flavours: a SendXAsync that returns as soon
    // as the transaction hash is known, and the original XAsync convenience that also
    // waits for the receipt. Callers that must survive a lost receipt (see
    // SwapMetadata.EvmLockTxId) use the split form so they can persist the hash between
    // the two steps; everything else keeps using the one-shot form.

    /// <summary>
    /// Broadcasts <c>ERC20Swap.lock</c> and returns the transaction hash without waiting for
    /// the receipt. Pair with <see cref="WaitForReceiptAsync"/>.
    /// </summary>
    public Task<string> SendLockAsync(
        byte[] preimageHash, BigInteger amount, string tokenAddress, string claimAddress, BigInteger timelock,
        CancellationToken ct = default) =>
        _nonceGuard.BroadcastAsync(
            () => _web3.Eth.GetContractHandler(Erc20SwapAddress).SendRequestAsync(new LockFunction
                {
                    PreimageHash = preimageHash,
                    Amount = amount,
                    TokenAddress = tokenAddress,
                    ClaimAddress = claimAddress,
                    Timelock = timelock,
                }), ct);

    /// <summary>Locks <paramref name="amount"/> of <paramref name="tokenAddress"/> in <c>ERC20Swap</c> for the given preimage hash.</summary>
    public async Task<TransactionReceipt> LockAsync(
        byte[] preimageHash, BigInteger amount, string tokenAddress, string claimAddress, BigInteger timelock,
        CancellationToken ct = default) =>
        await WaitForReceiptAsync(
            await SendLockAsync(preimageHash, amount, tokenAddress, claimAddress, timelock, ct), ct);

    /// <summary>
    /// Broadcasts <c>ERC20Swap.claim</c> and returns the transaction hash without waiting for
    /// the receipt. Pair with <see cref="WaitForReceiptAsync"/>.
    /// </summary>
    public Task<string> SendClaimAsync(
        byte[] preimage, BigInteger amount, string tokenAddress, string refundAddress, BigInteger timelock,
        CancellationToken ct = default) =>
        _nonceGuard.BroadcastAsync(
            () => _web3.Eth.GetContractHandler(Erc20SwapAddress).SendRequestAsync(new ClaimFunction
                {
                    Preimage = preimage,
                    Amount = amount,
                    TokenAddress = tokenAddress,
                    RefundAddress = refundAddress,
                    Timelock = timelock,
                }), ct);

    /// <summary>Claims tokens locked in <c>ERC20Swap</c> by revealing the preimage.</summary>
    public async Task<TransactionReceipt> ClaimAsync(
        byte[] preimage, BigInteger amount, string tokenAddress, string refundAddress, BigInteger timelock,
        CancellationToken ct = default) =>
        await WaitForReceiptAsync(
            await SendClaimAsync(preimage, amount, tokenAddress, refundAddress, timelock, ct), ct);

    /// <summary>
    /// Polls for <paramref name="txHash"/>'s receipt until it lands or
    /// <paramref name="timeout"/> elapses. Nethereum's own
    /// <c>SendRequestAndWaitForReceiptAsync</c> polls without an upper bound, which turns a
    /// dropped transaction into an indefinite hang inside a poll-loop tick; this bounds it and
    /// surfaces a timeout the caller can act on. A timeout does NOT mean the transaction
    /// failed — it may still confirm later, which is exactly why callers persist the hash
    /// before waiting.
    /// </summary>
    public Task<TransactionReceipt> WaitForReceiptAsync(
        string txHash, CancellationToken ct = default, TimeSpan? timeout = null, TimeSpan? pollInterval = null) =>
        EvmReceipts.WaitAsync(_web3, txHash, ct, timeout, pollInterval);

    /// <summary>Refunds tokens locked in <c>ERC20Swap</c> once <paramref name="timelock"/> has passed.</summary>
    public async Task<TransactionReceipt> RefundAsync(
        byte[] preimageHash, BigInteger amount, string tokenAddress, string claimAddress, BigInteger timelock,
        CancellationToken ct = default) =>
        await WaitForReceiptAsync(
            await SendRefundAsync(preimageHash, amount, tokenAddress, claimAddress, timelock, ct), ct);

    /// <summary>
    /// Broadcasts <c>ERC20Swap.refund</c> and returns the transaction hash without waiting for
    /// the receipt. Pair with <see cref="WaitForReceiptAsync"/>.
    /// </summary>
    public Task<string> SendRefundAsync(
        byte[] preimageHash, BigInteger amount, string tokenAddress, string claimAddress, BigInteger timelock,
        CancellationToken ct = default) =>
        _nonceGuard.BroadcastAsync(
            () => _web3.Eth.GetContractHandler(Erc20SwapAddress).SendRequestAsync(new RefundFunction
                {
                    PreimageHash = preimageHash,
                    Amount = amount,
                    TokenAddress = tokenAddress,
                    ClaimAddress = claimAddress,
                    Timelock = timelock,
                }), ct);

    // TODO: FindLockupEventAsync/FindClaimEventAsync/FindRefundEventAsync all call
    // CreateFilterInput() with no fromBlock/toBlock and no topic filter on the indexed
    // preimageHash, so each call re-scans the ERC20Swap contract's entire log history and
    // filters client-side. Fine on a young regtest/testnet chain; would not scale against
    // Arbitrum mainnet's real history. Should filter server-side by the preimageHash topic and
    // bound fromBlock (e.g. to the block the swap was created, or a recent lookback window).

    /// <inheritdoc />
    public async Task<BigInteger> GetBlockNumberAsync(CancellationToken ct = default) =>
        (await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;

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
