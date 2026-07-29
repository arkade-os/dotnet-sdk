using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Fees;
using NArk.Core.Helpers;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Evm.Dex;
using NArk.Swaps.Evm.Models;
using NArk.Swaps.Extensions;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;

namespace NArk.Swaps.Evm;

/// <summary>
/// Lock and claim paths for both legs — the EVM-side <c>ERC20Swap</c> lock/claim (plain tBTC
/// and the Milestone 4 DEX-hop variants). Mirrors <c>BoltzSwapProvider.Claims.cs</c>&apos;s split.
/// </summary>
public partial class EvmChainSwapProvider
{
    /// <summary>
    /// Locks <paramref name="result"/>'s tBTC amount in <c>ERC20Swap</c> for a
    /// <c>ChainEvmToArk</c> swap created via <see cref="CreateEvmToArkSwapAsync"/> — approve +
    /// lock in one call, using the claim address/timelock/amount Boltz returned in
    /// <c>result.Swap.LockupDetails</c>. Unlike the ARK/BTC legs (where the swap-creation
    /// response describes an address the counterparty pays into), the EVM leg's lock
    /// parameters are ones <em>we</em> choose when calling the contract — Boltz's response just
    /// tells us its own claim address so Boltz can later claim what we lock.
    /// </summary>
    /// <remarks>
    /// Idempotent — safe to retry. <c>ERC20Swap</c> reverts on a second lock for the same
    /// preimage hash, so a blind retry after a lost receipt would turn a <em>successful</em>
    /// lock into a hard failure and strand the funds. Three cases are resolved before
    /// broadcasting anything:
    /// <list type="number">
    ///   <item><description>a <c>Lockup</c> event already on-chain — nothing to do;</description></item>
    ///   <item><description>a hash recorded in <see cref="SwapMetadata.EvmLockTxId"/> but no event
    ///   yet — the transaction is still in flight, so wait for its receipt rather than
    ///   broadcasting a competing one;</description></item>
    ///   <item><description>neither — approve, broadcast, record the hash, then wait.</description></item>
    /// </list>
    /// </remarks>
    public async Task LockEvmAsync(EvmChainSwapResult result, CancellationToken ct = default)
    {
        if (result.Swap.LockupDetails is not { ClaimAddress: { } claimAddress } lockupDetails)
            throw new InvalidOperationException(
                $"Chain swap {result.Swap.Id}: missing EVM lockup details (claimAddress) — not a ChainEvmToArk swap?");

        var client = await GetEvmChainClientAsync(ct);
        var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
        var tokenAddress = info.Tokens[_options.PairCurrency];

        var existing = await client.FindLockupEventAsync(result.PreimageHash, ct);
        var pendingTxHash = await GetRecordedEvmTxIdAsync(result.Swap.Id, SwapMetadata.EvmLockTxId, ct);

        switch (EvmIdempotencyResolver.ResolveLock(existing is not null, pendingTxHash))
        {
            case EvmTxAction.AlreadyDone:
                _logger?.LogInformation(
                    "Swap {SwapId}: ERC20Swap lockup for this preimage hash is already on-chain ({Amount}) — lock is done",
                    result.Swap.Id, existing!.Amount);
                return;

            case EvmTxAction.AwaitBroadcast:
                _logger?.LogInformation(
                    "Swap {SwapId}: lock tx {TxHash} was already broadcast but hasn't been indexed yet — " +
                    "waiting for its receipt instead of broadcasting a second lock",
                    result.Swap.Id, pendingTxHash);
                await client.WaitForReceiptAsync(pendingTxHash!, ct);
                return;
        }

        await client.ApproveTokenAsync(tokenAddress, lockupDetails.Amount, ct);

        var txHash = await client.SendLockAsync(result.PreimageHash, lockupDetails.Amount, tokenAddress,
            claimAddress, lockupDetails.TimeoutBlockHeight, ct);
        await RecordEvmTxIdAsync(result.Swap.Id, SwapMetadata.EvmLockTxId, txHash, ct);

        await client.WaitForReceiptAsync(txHash, ct);

        _logger?.LogInformation("Swap {SwapId}: locked {Amount} tBTC in ERC20Swap for Boltz to claim (tx {TxHash})",
            result.Swap.Id, lockupDetails.Amount, txHash);
    }

    /// <summary>
    /// Whether this swap's EVM leg has any funds committed on-chain — either a confirmed
    /// <c>Lockup</c> event, or a broadcast lock transaction recorded in
    /// <see cref="SwapMetadata.EvmLockTxId"/> that hasn't been indexed yet.
    /// </summary>
    /// <remarks>
    /// Exists for the failure path in <c>InitiateEvmToArkChainSwap</c>: an exception out of
    /// <see cref="LockEvmAsync"/> does <em>not</em> imply the funds are safe, so the caller must
    /// ask this before marking a swap <see cref="ArkSwapStatus.Failed"/>. A failed swap drops out
    /// of the poll loop, and a swap that drops out of the poll loop never gets refunded.
    /// </remarks>
    public async Task<bool> HasCommittedEvmLockupAsync(EvmChainSwapResult result, CancellationToken ct = default)
    {
        if (await GetRecordedEvmTxIdAsync(result.Swap.Id, SwapMetadata.EvmLockTxId, ct) is not null)
            return true;

        try
        {
            var client = await GetEvmChainClientAsync(ct);
            return await client.FindLockupEventAsync(result.PreimageHash, ct) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Can't prove the funds are safe, so assume they aren't — the conservative answer
            // keeps the swap active and refundable rather than closing it out.
            _logger?.LogWarning(ex,
                "Swap {SwapId}: could not probe for an EVM lockup; assuming one may exist", result.Swap.Id);
            return true;
        }
    }

    /// <summary>
    /// Milestone 4 alternative to <see cref="LockEvmAsync"/>: funds the same
    /// <c>ChainEvmToArk</c> lockup from an arbitrary ERC20 (e.g. USDT) instead of tBTC directly,
    /// via <see cref="DEXSwapService.LockViaDexHopAsync"/> — one atomic transaction that pulls
    /// <paramref name="tokenInAddress"/> via Permit2, swaps it to tBTC, and locks the result.
    /// This provider's own <see cref="EvmAddress"/> both signs the Permit2 witness and is used
    /// as the refund address.
    /// </summary>
    // TODO: NOT idempotent, unlike LockEvmAsync. This path still broadcasts blind: no Lockup-event
    // probe, no EvmLockTxId recorded before the receipt wait, so a retry after a lost receipt
    // re-runs the whole DEX hop and the lock reverts on the duplicate preimage hash — while the
    // first attempt's funds sit locked on-chain. The fix is the three-step resolve LockEvmAsync
    // already uses (EvmIdempotencyResolver.ResolveLock); it needs DEXSwapService.LockViaDexHopAsync
    // split into broadcast + receipt-wait first, the same way EvmChainClient was.
    // TODO: also still not reachable from SwapsManagementServiceEvmExtensions/the normal
    // swap-creation flow, and a production IDexQuoteProvider is unwired (see that interface's TODO).
    public async Task LockEvmFromErc20Async(
        EvmChainSwapResult result, string tokenInAddress, BigInteger amountIn, CancellationToken ct = default)
    {
        if (_dexSwapService is null)
            throw new InvalidOperationException(
                "No DEXSwapService configured for this provider — pass one to EvmChainSwapProvider's constructor.");
        if (result.Swap.LockupDetails is not { ClaimAddress: { } claimAddress } lockupDetails)
            throw new InvalidOperationException(
                $"Chain swap {result.Swap.Id}: missing EVM lockup details (claimAddress) — not a ChainEvmToArk swap?");

        var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
        var tokenAddress = info.Tokens[_options.PairCurrency];
        var ownerKey = new EthECKey(_options.PrivateKey);

        await _dexSwapService.LockViaDexHopAsync(
            ownerKey, tokenInAddress, amountIn, tokenAddress, result.PreimageHash, claimAddress, EvmAddress,
            lockupDetails.TimeoutBlockHeight,
            permit2Nonce: new BigInteger(RandomUtils.GetBytes(8), isUnsigned: true),
            permit2Deadline: DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
            ct: ct);

        _logger?.LogInformation(
            "Swap {SwapId}: swapped {AmountIn} of {TokenIn} to tBTC via Router and locked it for Boltz to claim",
            result.Swap.Id, amountIn, tokenInAddress);
    }

    /// <summary>
    /// Milestone 4 alternative to the automatic claim path (<see cref="TryClaimEvmLockupAsync"/>,
    /// which the poll loop always uses): claims this swap's tBTC lockup and atomically swaps the
    /// proceeds to <paramref name="outputTokenAddress"/> via
    /// <see cref="DEXSwapService.ClaimAndSwapAsync"/>, instead of keeping tBTC. Returns the
    /// amount swept in the output token.
    /// </summary>
    // TODO: caller-invoked only — the poll loop has no way to know a caller wants this instead of
    // the plain claim (would need a new SwapMetadata field recording the desired output token,
    // consulted by PollSwapAsync/TryClaimEvmLockupAsync — not designed yet). A real
    // IDexQuoteProvider is also still unimplemented, same as LockEvmFromErc20Async.
    public async Task<BigInteger> ClaimEvmLockupToErc20Async(
        ArkSwap swap, string outputTokenAddress, CancellationToken ct = default)
    {
        if (_dexSwapService is null)
            throw new InvalidOperationException(
                "No DEXSwapService configured for this provider — pass one to EvmChainSwapProvider's constructor.");

        var preimageHex = swap.Get(SwapMetadata.Preimage)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: missing preimage in metadata.");
        var preimage = Convert.FromHexString(preimageHex);

        var client = await GetEvmChainClientAsync(ct);
        var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
        var tokenAddress = info.Tokens[_options.PairCurrency];
        var lockup = await client.FindLockupEventAsync(Hashes.SHA256(preimage), ct)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: no Lockup event found yet.");

        var claimKey = new EthECKey(_options.PrivateKey);
        var swept = await _dexSwapService.ClaimAndSwapAsync(
            claimKey, preimage, lockup.Amount, tokenAddress, lockup.RefundAddress, lockup.Timelock,
            outputTokenAddress, ct);

        await MarkSwapTerminalAsync(swap, ArkSwapStatus.Settled, null, ct);
        _logger?.LogInformation("Swap {SwapId}: claimed EVM lockup and swapped {Swept} to {OutputToken}",
            swap.SwapId, swept, outputTokenAddress);
        return swept;
    }

    private async Task TryClaimEvmLockupAsync(ArkSwap swap, CancellationToken ct)
    {
        var preimageHex = swap.Get(SwapMetadata.Preimage)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: missing preimage in metadata.");
        var preimage = Convert.FromHexString(preimageHex);

        var preimageHash = Hashes.SHA256(preimage);
        var client = await GetEvmChainClientAsync(ct);
        var info = await EvmChainClient.GetChainInfoAsync(_boltzClient, _options.PairCurrency, ct);
        var tokenAddress = info.Tokens[_options.PairCurrency];

        // Idempotency, same shape as LockEvmAsync: the poll loop and the websocket-triggered
        // poll can both land on this swap, and a retry after a lost receipt must not re-broadcast
        // a claim the contract will revert (the swap entry is deleted once claimed).
        var claimedOnChain = await client.FindClaimEventAsync(preimageHash, ct) is not null;
        var pendingClaimTx = swap.Get(SwapMetadata.EvmClaimTxId);

        switch (EvmIdempotencyResolver.ResolveClaim(claimedOnChain, pendingClaimTx))
        {
            case EvmTxAction.AlreadyDone:
                _logger?.LogInformation(
                    "Swap {SwapId}: ERC20Swap claim for this preimage hash is already on-chain — settling", swap.SwapId);
                await MarkSwapTerminalAsync(swap, ArkSwapStatus.Settled, null, ct);
                return;

            case EvmTxAction.AwaitBroadcast:
                _logger?.LogInformation(
                    "Swap {SwapId}: claim tx {TxHash} already broadcast — waiting for its receipt",
                    swap.SwapId, pendingClaimTx);
                await client.WaitForReceiptAsync(pendingClaimTx!, ct);
                await MarkSwapTerminalAsync(swap, ArkSwapStatus.Settled, null, ct);
                return;
        }

        // Amount/refundAddress/timelock come from Boltz's Lockup event, not our own records —
        // Boltz is the one who locked this side of the swap. A null here (classifier already
        // saw Boltz report the lockup as mempool/confirmed) means our own indexer view just
        // hasn't caught up yet — transient, so throwing and retrying next tick is correct,
        // unlike the permanent "never locked" case in TryRefundEvmLockupAsync below.
        var lockup = await client.FindLockupEventAsync(preimageHash, ct)
            ?? throw new InvalidOperationException($"Swap {swap.SwapId}: no Lockup event found yet.");

        var txHash = await client.SendClaimAsync(
            preimage, lockup.Amount, tokenAddress, lockup.RefundAddress, lockup.Timelock, ct);
        await RecordEvmTxIdAsync(swap.SwapId, SwapMetadata.EvmClaimTxId, txHash, ct);

        await client.WaitForReceiptAsync(txHash, ct);

        // Set status ourselves rather than waiting for Boltz's own indexer to notice we spent
        // its lockup and flip transaction.claimed — Boltz has strong incentive to track this
        // promptly (it's their funds moving) but nothing here should depend on an external
        // party's monitoring being fast, or even present.
        await MarkSwapTerminalAsync(swap, ArkSwapStatus.Settled, null, ct);
        _logger?.LogInformation("Swap {SwapId}: claimed EVM lockup (tx {TxHash})", swap.SwapId, txHash);
    }
}
