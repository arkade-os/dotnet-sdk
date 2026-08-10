using NBitcoin;

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
/// for its confirmation status; maturity is evaluated from
/// <see cref="ExitDelay"/> against that confirmation.</param>
/// <param name="CsvDelay">CSV delay in <b>blocks</b>, for a height-based
/// <see cref="ExitDelay"/>; <c>0</c> when the server's delay is time-based
/// (a block count is meaningless there — read <see cref="ExitDelay"/>
/// instead). Maturity height is <c>confirmHeight + CsvDelay</c>.</param>
/// <param name="ExitSequence">Raw BIP-68 <c>nSequence</c> of the server's
/// unilateral-exit delay at broadcast time, captured so the claim path can
/// tell a height-based lock from a time-based one without a second server
/// call. Prefer the decoded <see cref="ExitDelay"/> over this field.</param>
public record ExitPlan(
    string WalletId,
    string VtxoTxid,
    uint VtxoVout,
    string ClaimAddress,
    string LeafTxid,
    int CsvDelay,
    uint ExitSequence = 0)
{
    /// <summary>
    /// The server's unilateral-exit delay as a BIP-68 relative timelock.
    /// Branch on <see cref="Sequence.LockType"/> before doing any arithmetic
    /// with it: a time-based lock stores 512-second units with bit 22 set, so
    /// its raw <see cref="Sequence.Value"/> is not a block count.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The plan predates <see cref="ExitSequence"/> and its <see cref="CsvDelay"/>
    /// is outside the block range BIP 68 can encode — the signature of a plan
    /// captured by an older SDK against a time-based server, where the recorded
    /// delay was a raw nSequence misread as blocks. Re-run
    /// <c>BroadcastExitChainAsync</c> to capture a correct plan.
    /// </exception>
    public Sequence ExitDelay
    {
        get
        {
            if (ExitSequence != 0)
                return new Sequence(ExitSequence);

            // Plan captured before ExitSequence existed: CsvDelay was the only
            // record of the delay and was only ever meaningful block-based.
            if (CsvDelay is < 0 or > 0xFFFF)
                throw new InvalidOperationException(
                    $"ExitPlan for VTXO {VtxoTxid}:{VtxoVout} records CsvDelay={CsvDelay}, which is not a " +
                    "valid BIP-68 block count. This plan was captured by an older SDK against a server " +
                    "advertising a time-based unilateral-exit delay, and its delay is unusable. " +
                    "Re-run BroadcastExitChainAsync to capture a correct plan.");

            return new Sequence(CsvDelay);
        }
    }
}
