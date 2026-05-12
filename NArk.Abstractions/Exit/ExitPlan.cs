namespace NArk.Abstractions.Exit;

/// <summary>
/// Carries the minimum state the SDK needs to advance from a broadcast
/// chain to a matured claim. Returned by
/// <c>UnilateralExitService.BroadcastExitChainAsync</c> and consumed by
/// <c>UnilateralExitService.ClaimMaturedExitAsync</c>.
/// <para>
/// The stateless exit path doesn't persist anything itself — the caller
/// owns this record. Serialize it however you like (JSON, a settings
/// entry, a file on disk) and feed it back once you believe the CSV
/// timelock has matured. The SDK re-derives any other state (chain
/// confirmation, current block height, contract data, fee rate) at
/// claim time from the live broadcaster and configured stores.
/// </para>
/// </summary>
/// <param name="WalletId">Owning wallet — used to look up the signer
/// and the contract for the VTXO at claim time.</param>
/// <param name="VtxoTxid">VTXO parent txid (hex).</param>
/// <param name="VtxoVout">VTXO parent output index.</param>
/// <param name="ClaimAddress">Bitcoin address that receives the funds on
/// successful claim. Encoded as a string so the record stays serialisable
/// across networks without carrying network metadata.</param>
/// <param name="LeafTxid">Txid of the leaf virtual tx whose confirmation
/// starts the CSV countdown. The caller can poll an explorer / broadcaster
/// for its confirmation height; CSV maturity is at
/// <c>confirmHeight + CsvDelay</c>.</param>
/// <param name="CsvDelay">CSV delay (in blocks) from the server config at
/// broadcast time. Captured here so the caller can compute the maturity
/// height without a second server call.</param>
public record ExitPlan(
    string WalletId,
    string VtxoTxid,
    uint VtxoVout,
    string ClaimAddress,
    string LeafTxid,
    int CsvDelay);
