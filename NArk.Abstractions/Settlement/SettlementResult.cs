using NBitcoin;

namespace NArk.Abstractions.Settlement;

/// <summary>
/// The outcome of a completed settlement.
/// </summary>
/// <param name="TransferId">
/// Implementation-defined identifier for the transfer — an Arkade transaction id, an intent
/// id, a swap id. Stable enough to correlate later status updates against.
/// </param>
/// <param name="SourceAmount">
/// What was actually taken from the wallet, in the request's source asset atomic units —
/// satoshis for BTC.
/// </param>
/// <param name="DestinationAmountSats">
/// Satoshis expected at the destination. Equal to <paramref name="SourceAmount"/> for
/// same-asset BTC settlements; for a swap it is the amount net of swap and miner fees. For an
/// asset settlement it is the satoshi carrier the asset rides on, with the asset amount itself
/// reported in <paramref name="DestinationAtomicAmount"/>.
/// </param>
/// <param name="FeesPaidSats">Total fees paid, in satoshis.</param>
/// <param name="TransactionId">The Arkade transaction id, when the settlement produced one directly.</param>
/// <param name="DestinationAtomicAmount">
/// Expected amount in the destination asset's own atomic units, for destinations that are
/// not denominated in satoshis (a stablecoin transfer, for example). <see langword="null"/>
/// when the destination is BTC.
/// </param>
public record SettlementResult(
    string TransferId,
    long SourceAmount,
    long DestinationAmountSats,
    long FeesPaidSats,
    uint256? TransactionId = null,
    long? DestinationAtomicAmount = null);
