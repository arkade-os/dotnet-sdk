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
    // TODO: demo-only. Replace with a pluggable signer abstraction (e.g. KMS/HSM-backed,
    // or delegate to the same key-management story the Ark side uses) before any real
    // deployment — a raw private key sitting in configuration is not production-safe.
    public required string PrivateKey { get; set; }

    /// <summary>
    /// How often to poll Boltz for swap status changes — the fallback safety net; the
    /// persistent websocket connection is the primary, near-real-time mechanism.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    // ── Timelock validation (see EvmSwapTimeoutValidator) ────────────────────
    // The two legs count time in different units, so validating their ordering means
    // converting both to wall clock. These are the conversion factors and the margins that
    // absorb their error. Defaults target Arbitrum + Bitcoin; a chain with a different
    // cadence, or a regtest whose blocks are mined on demand rather than on a schedule,
    // needs them adjusted or validation will misjudge perfectly good timeouts.

    /// <summary>Average block time of the EVM chain. Default targets Arbitrum.</summary>
    public TimeSpan EvmBlockTime { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Average block time backing the Arkade leg's block-height timelocks (Bitcoin).
    /// Unused when the VHTLC's refund locktime is expressed as a timestamp rather than a height.
    /// </summary>
    public TimeSpan ArkadeBlockTime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Minimum time we insist on having to claim the counterparty's leg before it expires.
    /// </summary>
    public TimeSpan MinClaimWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Minimum gap between the counterparty's leg expiring and our own lockup becoming
    /// refundable. Absorbs block-time estimation error on both chains.
    /// </summary>
    public TimeSpan MinTimeoutOrderingMargin { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether a timelock-validation failure aborts swap creation. Left on: creating the swap
    /// anyway means committing funds to an arrangement whose atomicity we've just disproved.
    /// Turn it off only to diagnose an environment whose block cadence makes the wall-clock
    /// conversion meaningless (a regtest mining on demand, say) — violations are then logged
    /// instead.
    /// </summary>
    public bool EnforceTimeoutValidation { get; set; } = true;
}
