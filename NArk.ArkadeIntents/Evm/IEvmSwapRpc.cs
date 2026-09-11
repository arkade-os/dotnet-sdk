using System.Numerics;

namespace NArk.ArkadeIntents.Evm;

/// <summary>Read-only EVM operations required to prove and verify an ERC20Swap claim.</summary>
public interface IEvmSwapRpc
{
    /// <summary>Reads the connected chain identifier.</summary>
    Task<BigInteger> GetChainIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the current block height.</summary>
    Task<BigInteger> GetBlockNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one block's unix timestamp.</summary>
    Task<long> GetBlockTimestampAsync(BigInteger blockNumber, CancellationToken cancellationToken = default);

    /// <summary>Executes an <c>eth_call</c>, optionally at a historical height.</summary>
    Task<byte[]> CallAsync(
        string to,
        byte[] data,
        BigInteger? blockNumber = null,
        CancellationToken cancellationToken = default);

    /// <summary>Waits for a transaction receipt.</summary>
    Task<EvmTransactionReceipt> WaitForReceiptAsync(
        string transactionHash,
        CancellationToken cancellationToken = default);
}

/// <summary>Submits an EVM transaction without exposing key or fee management to the SDK.</summary>
public interface IEvmTransactionSender
{
    /// <summary>Signs and submits one transaction.</summary>
    Task<string> SendAsync(EvmTransactionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A contract call to sign and submit.</summary>
/// <param name="ChainId">Expected EIP-155 chain identifier.</param>
/// <param name="To">Destination contract.</param>
/// <param name="Data">ABI calldata.</param>
public sealed record EvmTransactionRequest(BigInteger ChainId, string To, byte[] Data);

/// <summary>One EVM log entry.</summary>
/// <param name="Address">Contract that emitted the log.</param>
/// <param name="Topics">Ordered log topics.</param>
/// <param name="Data">Hex-encoded log data.</param>
public sealed record EvmLog(string Address, IReadOnlyList<string> Topics, string Data);

/// <summary>A mined EVM transaction receipt.</summary>
/// <param name="TransactionHash">Transaction identifier.</param>
/// <param name="Succeeded">Whether receipt status is one.</param>
/// <param name="Logs">Logs emitted by the transaction.</param>
public sealed record EvmTransactionReceipt(
    string TransactionHash,
    bool Succeeded,
    IReadOnlyList<EvmLog> Logs);
