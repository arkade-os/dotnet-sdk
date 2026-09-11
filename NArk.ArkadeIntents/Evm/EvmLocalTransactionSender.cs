using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;

namespace NArk.ArkadeIntents.Evm;

/// <summary>Fee, gas, and sender limits for locally signed type-2 transactions.</summary>
public sealed class EvmTransactionSenderOptions
{
    /// <summary>Configured gas-payer address that the private key must derive.</summary>
    public required string ExpectedSenderAddress { get; init; }
    /// <summary>Multiplier applied to the latest base fee; defaults to two.</summary>
    public int BaseFeeMultiplier { get; init; } = 2;
    /// <summary>Gas estimate multiplier of at least 10,000 basis points; defaults to 12,000.</summary>
    public int GasLimitBasisPoints { get; init; } = 12_000;
    /// <summary>Maximum accepted EIP-1559 fee per gas, in wei.</summary>
    public required BigInteger MaxFeePerGasWei { get; init; }
    /// <summary>Maximum accepted priority fee per gas, in wei.</summary>
    public required BigInteger MaxPriorityFeePerGasWei { get; init; }
    /// <summary>Maximum transaction gas limit.</summary>
    public required BigInteger MaxGasLimit { get; init; }

    internal string Validate()
    {
        var sender = EvmWire.RequireNonZeroAddress(
            ExpectedSenderAddress, nameof(ExpectedSenderAddress), lowerCase: true);
        if (BaseFeeMultiplier < 1 || GasLimitBasisPoints < 10_000 || MaxFeePerGasWei <= 0
            || MaxPriorityFeePerGasWei <= 0 || MaxPriorityFeePerGasWei > MaxFeePerGasWei
            || MaxGasLimit <= 0)
            throw new ArgumentException("EVM fee and gas limits must be positive and cannot shrink estimates");
        return sender;
    }
}

/// <summary>A local EVM transaction could not be safely constructed or submitted.</summary>
/// <param name="message">Failure reason without key material.</param>
public sealed class EvmTransactionException(string message) : Exception(message);

/// <summary>Serializes nonce allocation and locally signs EIP-1559 transactions.</summary>
/// <remarks>Nonce allocation is serialized by gas-payer address within this process. Assign each
/// key to one process unless the deployment supplies an external nonce coordinator. The sender
/// retains signing key material but exposes neither the raw key nor a serialized representation.</remarks>
public sealed class EvmLocalTransactionSender : IEvmDurableTransactionSender
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SendGates = new();
    private readonly EvmJsonRpcClient _rpc;
    private readonly EvmTransactionSenderOptions _options;
    private readonly EthECKey _key;
    private readonly SemaphoreSlim _sendGate;

    /// <summary>Creates a sender and clears its temporary copy of the supplied key.</summary>
    /// <param name="rpc">Concrete JSON-RPC client used for chain data and broadcast.</param>
    /// <param name="privateKey">A 32-byte secp256k1 key whose owner must clear its source buffer.</param>
    /// <param name="options">Expected address and transaction ceilings.</param>
    public EvmLocalTransactionSender(
        EvmJsonRpcClient rpc,
        ReadOnlySpan<byte> privateKey,
        EvmTransactionSenderOptions options)
    {
        if (privateKey.Length != 32)
            throw new ArgumentException("EVM private key must be exactly 32 bytes", nameof(privateKey));
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var expected = _options.Validate();
        var keyBytes = privateKey.ToArray();
        try
        {
            try
            {
                _key = new EthECKey(keyBytes, true);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw new ArgumentException("EVM private key is not a valid secp256k1 scalar", nameof(privateKey));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
        Address = _key.GetPublicAddress().ToLowerInvariant();
        if (!Address.Equals(expected, StringComparison.Ordinal))
            throw new ArgumentException("EVM private key does not derive the configured sender address", nameof(privateKey));
        _sendGate = SendGates.GetOrAdd(Address, static _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>Derived lowercase gas-payer address.</summary>
    public string Address { get; }

    /// <inheritdoc />
    public async Task<string> SendAsync(
        EvmTransactionRequest request,
        CancellationToken cancellationToken = default) =>
        await SendCoreAsync(request, null, cancellationToken);

    /// <inheritdoc />
    public async Task<string> SendAsync(
        EvmTransactionRequest request,
        Func<EvmPreparedTransaction, CancellationToken, Task> onPrepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onPrepared);
        return await SendCoreAsync(request, onPrepared, cancellationToken);
    }

    private async Task<string> SendCoreAsync(
        EvmTransactionRequest request,
        Func<EvmPreparedTransaction, CancellationToken, Task>? onPrepared,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Data);
            var chainId = await _rpc.GetChainIdAsync(cancellationToken);
            if (request.ChainId <= 0 || request.ChainId != chainId)
                throw new EvmTransactionException("transaction chain id differs from the connected EVM node");
            var to = EvmWire.RequireNonZeroAddress(request.To, nameof(request.To), lowerCase: true);
            var nonce = await _rpc.GetPendingNonceAsync(Address, cancellationToken);
            var (baseFee, priorityFee) = await _rpc.GetFeeQuoteAsync(cancellationToken);
            var maxFee = checked(baseFee * _options.BaseFeeMultiplier + priorityFee);
            if (priorityFee > _options.MaxPriorityFeePerGasWei || maxFee > _options.MaxFeePerGasWei)
                throw new EvmTransactionException("node fee quote exceeds configured EVM limits");
            var estimate = await _rpc.EstimateGasAsync(
                Address, to, request.Data, maxFee, priorityFee, cancellationToken);
            var gasLimit = (estimate * _options.GasLimitBasisPoints + 9_999) / 10_000;
            if (estimate <= 0 || gasLimit > _options.MaxGasLimit)
                throw new EvmTransactionException("gas estimate exceeds configured EVM limit");
            var transaction = new Transaction1559(
                chainId, nonce, priorityFee, maxFee, gasLimit, to, BigInteger.Zero,
                "0x" + Convert.ToHexString(request.Data).ToLowerInvariant(), []);
            var raw = new Transaction1559Signer().SignTransaction(_key, transaction);
            if (!raw.StartsWith("0x", StringComparison.Ordinal))
                raw = "0x" + raw;
            raw = raw.ToLowerInvariant();
            if (onPrepared is null)
                return await _rpc.SendRawTransactionAsync(raw, cancellationToken);

            var preparedHash = "0x" + new Sha3Keccack().CalculateHashFromHex(raw);
            await onPrepared(new EvmPreparedTransaction(preparedHash, raw), cancellationToken);
            var submittedHash = await _rpc.SendRawTransactionAsync(raw, cancellationToken);
            if (!submittedHash.Equals(preparedHash, StringComparison.OrdinalIgnoreCase))
                throw new EvmTransactionException("node returned a different transaction hash");
            return preparedHash;
        }
        catch (OverflowException)
        {
            throw new EvmTransactionException("EVM transaction quantity overflowed");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> ResumeAsync(EvmTransactionRequest request, EvmPreparedTransaction prepared,
        CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            ValidatePrepared(request, prepared);
            var chainId = await _rpc.GetChainIdAsync(cancellationToken);
            if (request.ChainId <= 0 || request.ChainId != chainId)
                throw new EvmTransactionException("transaction chain id differs from the connected EVM node");
            try
            {
                var submitted = await _rpc.SendRawTransactionAsync(prepared.SignedTransaction, cancellationToken);
                if (!submitted.Equals(prepared.TransactionHash, StringComparison.OrdinalIgnoreCase))
                    throw new EvmTransactionException("node returned a different transaction hash");
            }
            catch (EvmJsonRpcException)
            {
                // A node may report an already-known or mined transaction as an RPC error.
            }
            return prepared.TransactionHash;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void ValidatePrepared(EvmTransactionRequest request, EvmPreparedTransaction prepared)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(request.Data);
        var raw = prepared.SignedTransaction;
        if (raw is null || !raw.StartsWith("0x02", StringComparison.Ordinal) || raw != raw.ToLowerInvariant()
            || raw.Length % 2 != 0 || !raw[2..].All(Uri.IsHexDigit)
            || prepared.TransactionHash != "0x" + new Sha3Keccack().CalculateHashFromHex(raw))
            throw new EvmTransactionException("prepared EVM transaction artifact is invalid");
        Transaction1559 transaction;
        try
        {
            transaction = (Transaction1559)TransactionFactory.CreateTransaction(raw);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidCastException)
        {
            throw new EvmTransactionException("prepared EVM transaction cannot be decoded");
        }
        var expectedTo = EvmWire.RequireNonZeroAddress(request.To, nameof(request.To), lowerCase: true);
        var sender = EthECKeyBuilderFromSignedTransaction.GetEthECKey(transaction).GetPublicAddress();
        if (transaction.ChainId != request.ChainId || transaction.ReceiverAddress is null
            || !transaction.ReceiverAddress.Equals(expectedTo, StringComparison.OrdinalIgnoreCase)
            || transaction.Amount != BigInteger.Zero
            || transaction.Data is null
            || !transaction.Data.Equals("0x" + Convert.ToHexString(request.Data).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
            || !sender.Equals(Address, StringComparison.OrdinalIgnoreCase)
            || transaction.MaxFeePerGas is null || transaction.MaxFeePerGas > _options.MaxFeePerGasWei
            || transaction.MaxPriorityFeePerGas is null || transaction.MaxPriorityFeePerGas > _options.MaxPriorityFeePerGasWei
            || transaction.GasLimit is null || transaction.GasLimit > _options.MaxGasLimit)
            throw new EvmTransactionException("prepared EVM transaction differs from the requested claim");
    }

    /// <inheritdoc />
    public override string ToString() => $"{nameof(EvmLocalTransactionSender)} ({Address})";
}
