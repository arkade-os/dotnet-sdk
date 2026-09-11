using System.Numerics;
using System.Text.Json.Serialization;
using NArk.ArkadeIntents.Rfq.Converters;

namespace NArk.ArkadeIntents.Rfq.Profiles.Evm;

/// <summary>Compare-only fields returned by the EVM send corridor.</summary>
public sealed class EvmSendQuoteProfile
{
    /// <summary>Echo of the requested SHA-256 payment hash.</summary>
    public string? PaymentHash { get; init; }

    /// <summary>Solver-derived Arkade lockup address.</summary>
    public string? LockupAddress { get; init; }

    /// <summary>P2TR script receiving the solver's non-interactive Arkade claim.</summary>
    public string? ReceiverPkScript { get; init; }

    /// <summary>EVM block height at which the solver's ERC20 refund opens.</summary>
    [JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger? EvmTimeoutBlock { get; init; }

    /// <summary>Solver address that receives an expired ERC20 lock.</summary>
    public string? EvmRefundAddress { get; init; }

    /// <summary>ERC20Swap contract address.</summary>
    public string? EvmContractAddress { get; init; }

    /// <summary>EIP-155 chain identifier.</summary>
    [JsonConverter(typeof(AtomicAmountConverter))]
    public BigInteger? EvmChainId { get; init; }

    /// <summary>Required EVM lock confirmations.</summary>
    public int? MinConfirmations { get; init; }

    /// <summary>Required age of the depth-proving block, in seconds.</summary>
    public int? MinAgeSeconds { get; init; }
}
