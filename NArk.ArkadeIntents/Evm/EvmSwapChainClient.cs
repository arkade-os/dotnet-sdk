using System.Numerics;
using System.Security.Cryptography;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;

namespace NArk.ArkadeIntents.Evm;

/// <summary>Thrown when public EVM state does not prove a safe swap transition.</summary>
/// <param name="message">Proof failure reason.</param>
public sealed class EvmSwapProofException(string message) : Exception(message);

/// <summary>Evidence that a lock existed at the required depth and age.</summary>
/// <param name="ObservedAtBlock">Tip used for the observation.</param>
/// <param name="ProvenAtBlock">Historical block at which the lock was present.</param>
/// <param name="ProvenBlockTimestamp">Unix timestamp of the proving block.</param>
public sealed record EvmLockProof(
    BigInteger ObservedAtBlock,
    BigInteger ProvenAtBlock,
    long ProvenBlockTimestamp);

/// <summary>Verified ERC20 delivery resulting from claimFor.</summary>
/// <param name="TransactionHash">Successful claim transaction.</param>
/// <param name="DeliveredAmount">Exact amount proven by the canonical ERC20 Transfer event.</param>
/// <param name="Preimage">Preimage proven by the canonical Claim event.</param>
public sealed record EvmClaimResult(string TransactionHash, BigInteger DeliveredAmount, byte[] Preimage);

/// <summary>Proves an ERC20Swap lock and submits and verifies its permissionless claim.</summary>
public sealed class EvmSwapChainClient
{
    private readonly IEvmSwapRpc _rpc;
    private readonly IEvmTransactionSender _sender;
    private readonly EvmSendPolicy _policy;
    private readonly TimeProvider _time;

    /// <summary>Creates a client over host-provided read and signing seams.</summary>
    /// <param name="rpc">Read-only chain adapter.</param>
    /// <param name="sender">Host-controlled signer and broadcaster.</param>
    /// <param name="policy">Configured chain and proof facts.</param>
    /// <param name="timeProvider">Clock used for block-age proof.</param>
    public EvmSwapChainClient(
        IEvmSwapRpc rpc,
        IEvmTransactionSender sender,
        EvmSendPolicy policy,
        TimeProvider? timeProvider = null)
    {
        policy.Validate();
        _rpc = rpc;
        _sender = sender;
        _policy = policy;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Requires <c>swaps(key)</c> at tip and the configured depth probe.</summary>
    /// <param name="values">Validated six-field swap tuple.</param>
    /// <param name="cancellationToken">Cancels chain reads.</param>
    /// <returns>Public lock proof coordinates.</returns>
    public async Task<EvmLockProof> ProveLockAsync(
        Erc20SwapValues values,
        CancellationToken cancellationToken = default)
    {
        await AssertChainAsync(cancellationToken);
        ValidateValues(values);
        var tip = await _rpc.GetBlockNumberAsync(cancellationToken);
        if (tip < 0)
            throw new EvmSwapProofException("connected RPC returned a negative block height");
        if (values.TimeoutBlock <= tip)
            throw new EvmSwapProofException("ERC20 refund height has passed");
        if (!await IsLockedAsync(values, null, cancellationToken))
            throw new EvmSwapProofException("ERC20Swap lock is absent at the current tip");

        var probe = tip - _policy.MinConfirmations + 1;
        if (probe < 0)
            throw new EvmSwapProofException("chain is not deep enough for the requested proof");
        if (!await IsLockedAsync(values, probe, cancellationToken))
            throw new EvmSwapProofException("ERC20Swap lock is absent at the depth-proving block");
        var timestamp = await _rpc.GetBlockTimestampAsync(probe, cancellationToken);
        var age = _time.GetUtcNow().ToUnixTimeSeconds() - timestamp;
        if (age < _policy.MinAgeSeconds)
            throw new EvmSwapProofException("depth-proving block is too recent");
        return new EvmLockProof(tip, probe, timestamp);
    }

    /// <summary>Claims after proving the lock, then verifies receipt, events, and consumed state.</summary>
    /// <param name="values">Validated six-field swap tuple.</param>
    /// <param name="preimage">The 32-byte swap preimage.</param>
    /// <param name="cancellationToken">Cancels reads or submission.</param>
    /// <returns>Verified transaction-local delivery.</returns>
    public async Task<EvmClaimResult> ClaimForAsync(
        Erc20SwapValues values,
        byte[] preimage,
        CancellationToken cancellationToken = default)
    {
        await ProveLockAsync(values, cancellationToken);
        var currentBlock = await _rpc.GetBlockNumberAsync(cancellationToken);
        if (currentBlock < 0 || values.TimeoutBlock <= currentBlock
            || !await IsLockedAsync(values, null, cancellationToken))
            throw new EvmSwapProofException("ERC20Swap lock is not claimable immediately before signing");
        var data = Erc20SwapCodec.ClaimForCall(preimage, values);
        var txid = await _sender.SendAsync(
            new EvmTransactionRequest(_policy.ChainId, _policy.SwapContractAddress, data), cancellationToken);
        var receipt = await _rpc.WaitForReceiptAsync(txid, cancellationToken);
        if (!receipt.Succeeded || !receipt.TransactionHash.Equals(txid, StringComparison.OrdinalIgnoreCase))
            throw new EvmSwapProofException("claimFor transaction did not succeed");
        var revealed = VerifyClaimEvent(receipt, values, _policy.SwapContractAddress);
        if (!revealed.SequenceEqual(preimage))
            throw new EvmSwapProofException("Claim event revealed a different preimage");

        var delivered = VerifyTransferEvent(receipt, values, _policy.SwapContractAddress);
        if (await IsLockedAsync(values, null, cancellationToken))
            throw new EvmSwapProofException("ERC20Swap lock remains active after the claim");
        return new EvmClaimResult(txid, delivered, revealed);
    }

    private async Task AssertChainAsync(CancellationToken cancellationToken)
    {
        if (await _rpc.GetChainIdAsync(cancellationToken) != _policy.ChainId)
            throw new EvmSwapProofException("connected RPC has an unexpected chain id");
    }

    private async Task<bool> IsLockedAsync(
        Erc20SwapValues values,
        BigInteger? blockNumber,
        CancellationToken cancellationToken) => Erc20SwapCodec.ReadSwapsResult(await _rpc.CallAsync(
            _policy.SwapContractAddress, Erc20SwapCodec.SwapsCall(values), blockNumber, cancellationToken));

    private void ValidateValues(Erc20SwapValues values)
    {
        if (values.Amount <= 0 || values.TimeoutBlock <= 0)
            throw new EvmSwapProofException("swap amount and timeout must be positive");
        EvmWire.RequireNonZeroHex32(values.PaymentHash, nameof(values.PaymentHash));
        EvmWire.RequireNonZeroAddress(values.TokenAddress, nameof(values.TokenAddress), lowerCase: true);
        EvmWire.RequireNonZeroAddress(values.ClaimAddress, nameof(values.ClaimAddress), lowerCase: true);
        EvmWire.RequireNonZeroAddress(values.RefundAddress, nameof(values.RefundAddress), lowerCase: true);
        Erc20SwapCodec.SwapKey(values);
        if (!values.TokenAddress.Equals(_policy.TokenAddress, StringComparison.Ordinal))
            throw new EvmSwapProofException("swap token differs from the configured policy");
    }

    private static byte[] VerifyClaimEvent(
        EvmTransactionReceipt receipt,
        Erc20SwapValues values,
        string contractAddress)
    {
        var expectedHash = "0x" + values.PaymentHash;
        var matches = receipt.Logs.Where(log =>
            SameAddress(log.Address, contractAddress)
            && log.Topics.Count == 2
            && log.Topics[0].Equals(Erc20SwapCodec.ClaimTopic, StringComparison.OrdinalIgnoreCase)
            && log.Topics[1].Equals(expectedHash, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
            throw new EvmSwapProofException("receipt does not contain exactly one matching Claim event");
        var data = matches[0].Data;
        if (!data.StartsWith("0x", StringComparison.Ordinal) || data.Length != 66)
            throw new EvmSwapProofException("Claim event data is not a 32-byte preimage");
        var preimage = Convert.FromHexString(data[2..]);
        if (!SHA256.HashData(preimage).SequenceEqual(Erc20SwapCodec.Hex32(values.PaymentHash)))
            throw new EvmSwapProofException("Claim event preimage does not match the payment hash");
        return preimage;
    }

    private static BigInteger VerifyTransferEvent(
        EvmTransactionReceipt receipt,
        Erc20SwapValues values,
        string contractAddress)
    {
        var from = AddressTopic(contractAddress);
        var to = AddressTopic(values.ClaimAddress);
        var matches = receipt.Logs.Where(log =>
            SameAddress(log.Address, values.TokenAddress)
            && log.Topics.Count == 3
            && log.Topics[0].Equals(Erc20SwapCodec.TransferTopic, StringComparison.OrdinalIgnoreCase)
            && log.Topics[1].Equals(from, StringComparison.OrdinalIgnoreCase)
            && log.Topics[2].Equals(to, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
            throw new EvmSwapProofException("receipt does not contain exactly one matching ERC20 Transfer event");
        var data = matches[0].Data;
        if (!data.StartsWith("0x", StringComparison.Ordinal) || data.Length != 66
            || !data[2..].All(Uri.IsHexDigit))
            throw new EvmSwapProofException("ERC20 Transfer amount is not a canonical uint256");
        var amount = new BigInteger(Convert.FromHexString(data[2..]), true, true);
        if (amount != values.Amount)
            throw new EvmSwapProofException("ERC20 Transfer amount differs from the quoted amount");
        return amount;
    }

    private static string AddressTopic(string address) =>
        "0x" + new string('0', 24) + EvmWire.RequireNonZeroAddress(address, nameof(address))[2..];

    private static bool SameAddress(string address, string contractAddress) =>
        EvmWire.RequireAddress(address, nameof(address)).Equals(
            EvmWire.RequireAddress(contractAddress, nameof(contractAddress)), StringComparison.OrdinalIgnoreCase);
}
