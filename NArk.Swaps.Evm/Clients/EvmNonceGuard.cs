using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace NArk.Swaps.Evm;

/// <summary>
/// Serialises transaction broadcasts that share one EVM account, so concurrent swaps can't
/// collide on the same nonce.
/// </summary>
/// <remarks>
/// <para>
/// Nethereum's default transaction manager resolves the account's nonce independently for every
/// send. Two broadcasts in flight at the same time — the routine poll tick racing a
/// websocket-triggered poll for a different swap, or a lock racing a refund — therefore both
/// read the same pending count and sign the same nonce. One of the two is then dropped or
/// silently replaced by the node, and since a broadcast is how funds get committed, the loser is
/// a lock that never happened or a refund that never landed.
/// </para>
/// <para>
/// The guard is deliberately scoped to <em>broadcast only</em> — nonce resolution, signing and
/// the <c>eth_sendRawTransaction</c> call. Receipt waiting stays outside: it takes seconds to
/// minutes, and holding the gate across it would serialise every swap in the process behind the
/// slowest confirmation. This is why the clients split sending from waiting
/// (<see cref="EvmChainClient.SendLockAsync"/> / <see cref="EvmChainClient.WaitForReceiptAsync"/>)
/// rather than using Nethereum's combined <c>SendRequestAndWaitForReceiptAsync</c>.
/// </para>
/// <para>
/// One guard instance covers one account. Pass the <em>same</em> instance to every client that
/// signs with that key — <see cref="EvmChainClient"/> and <see cref="RouterClient"/> both do,
/// and the DEX-hop path drives both from the same
/// <see cref="EvmSwapOptions.PrivateKey"/>, so giving them separate guards would leave exactly
/// the race this type exists to close.
/// </para>
/// </remarks>
public sealed class EvmNonceGuard : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Runs <paramref name="broadcast"/> with exclusive access to the account's nonce, returning
    /// its transaction hash. Do not wait for a receipt inside <paramref name="broadcast"/>.
    /// </summary>
    public async Task<string> BroadcastAsync(Func<Task<string>> broadcast, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await broadcast();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// Shared receipt polling for the EVM clients.
/// </summary>
internal static class EvmReceipts
{
    /// <summary>
    /// Polls for <paramref name="txHash"/>'s receipt until it lands or <paramref name="timeout"/>
    /// elapses. Nethereum's own <c>SendRequestAndWaitForReceiptAsync</c> polls without an upper
    /// bound, which turns a dropped transaction into an indefinite hang inside a poll-loop tick;
    /// this bounds it and surfaces a timeout the caller can act on. A timeout does NOT mean the
    /// transaction failed — it may still confirm later, which is exactly why callers persist the
    /// hash before waiting.
    /// </summary>
    public static async Task<TransactionReceipt> WaitAsync(
        Web3 web3, string txHash, CancellationToken ct, TimeSpan? timeout, TimeSpan? pollInterval)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;
        var interval = pollInterval ?? TimeSpan.FromSeconds(2);

        while (true)
        {
            var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
            if (receipt is not null)
                return receipt;

            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Transaction {txHash} has no receipt after {effectiveTimeout.TotalSeconds:0}s. " +
                    "It may still confirm later — do not treat this as a failed broadcast.");

            await Task.Delay(interval, ct);
        }
    }
}
