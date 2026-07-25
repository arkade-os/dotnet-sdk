namespace NArk.Swaps.Evm;

/// <summary>
/// Configuration for <see cref="EvmChainSwapProvider"/>.
/// </summary>
public class EvmSwapOptions
{
    /// <summary>JSON-RPC URL of the EVM chain (Arbitrum for this milestone).</summary>
    public required string RpcUrl { get; set; }

    /// <summary>
    /// Boltz currency symbol for this EVM chain's tBTC token — used both as the chain-swap
    /// pair key (<c>/v2/swap/chain</c>) and, confusingly, as the "currency" path segment for
    /// the per-chain contracts lookup (<c>/v2/chain/{currency}/contracts</c> keys by asset
    /// symbol like <c>"TBTC"</c>/<c>"RBTC"</c>, not by chain name like <c>"arbitrum"</c>/
    /// <c>"rsk"</c> — verified against the live API).
    /// </summary>
    public string PairCurrency { get; set; } = "TBTC";

    /// <summary>
    /// Private key (hex, 0x-prefixed or not) of the EVM account this provider signs
    /// lock/claim/refund transactions with. Demo-only: a real integration would use a
    /// pluggable signer, not a raw private key in config.
    /// </summary>
    public required string PrivateKey { get; set; }

    /// <summary>
    /// How often to poll Boltz for swap status changes — the fallback safety net; the
    /// persistent websocket connection is the primary, near-real-time mechanism.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
}
