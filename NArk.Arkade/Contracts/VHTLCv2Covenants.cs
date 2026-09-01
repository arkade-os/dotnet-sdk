using NBitcoin.Secp256k1;

namespace NArk.Arkade.Contracts;

/// <summary>
/// The Arkade asset a <see cref="VHTLCv2Contract"/>'s covenant leaves bind, as the pair the
/// introspection opcodes take.
/// </summary>
/// <param name="GenesisTxid">
/// The asset's genesis transaction id, 32 bytes, in CANONICAL order — exactly the leading 32 bytes
/// of a serialized Asset ID, not reversed. The covenant reverses it internally, because the
/// introspection opcodes match wire order; a caller who pre-reverses gets a contract that is
/// unspendable on its covenant leaves and nothing in the failure says why.
/// </param>
/// <param name="GroupIndex">The asset group index within that genesis transaction, in [0, 65535].</param>
/// <remarks>
/// A canonical Asset ID is a <c>(genesis txid, group index)</c> pair, never a single blob, which is
/// why this is two fields rather than the <c>string</c> asset id the rest of the SDK passes around.
/// </remarks>
public sealed record VHTLCv2Asset(byte[] GenesisTxid, int GroupIndex);

/// <summary>
/// The opt-in bound that makes a non-interactive claim pay at least the quoted amounts, enforced in
/// script rather than at the consumer's own admission layer.
/// </summary>
/// <param name="Amount">Sats the claim must pay. Positive.</param>
/// <param name="AssetAmount">
/// Asset base units the claim must pay. Required if and only if the contract names a
/// <see cref="VHTLCv2Asset"/>: bounding only the sats would pin the CARRIER and say nothing about
/// the asset that is the actual amount — half-enforcement that reads like enforcement.
/// </param>
/// <remarks>
/// <para>
/// ADDITIVE, NEVER A REPLACEMENT. The covenant's default bound is conservation
/// (<c>out &gt;= in</c>), and it stays. On its own, <c>out &gt;= quoted</c> leaves everything ABOVE
/// the quote unconstrained, so an overfunded lockup's surplus could be routed anywhere by whoever
/// assembles the spend — trading an underfunding hole for a skimming one.
/// </para>
/// <para>
/// What it costs: the address becomes a function of the quote, because these amounts compile into
/// the leaf, hence the covenant key, hence the pkScript — a re-quote is a different address and
/// cannot be applied to a lockup already funded. And an underfunded lockup becomes unclaimable, its
/// only exit the refund path, which waits out the locktime.
/// </para>
/// </remarks>
public sealed record VHTLCv2StrictClaim(long Amount, long? AssetAmount = null);

/// <summary>
/// The optional <c>nonInteractiveClaim</c> leaf: the Arkade server plus a covenant-tweaked emulator
/// co-signer, pinned to <paramref name="ReceiverPkScript"/>. Lets the receiver's claim be pushed by
/// the emulator without the receiver being online.
/// </summary>
/// <param name="ReceiverPkScript">Where the claim must pay — the receiver's P2TR scriptPubKey (34 bytes).</param>
/// <param name="EmulatorPubKey">The emulator key the covenant tweaks.</param>
/// <param name="Strict">The opt-in quoted bound; omit for the default conservation bound alone.</param>
public sealed record VHTLCv2NonInteractiveClaim(
    byte[] ReceiverPkScript,
    ECXOnlyPubKey EmulatorPubKey,
    VHTLCv2StrictClaim? Strict = null);

/// <summary>
/// The optional <c>nonInteractiveRefund</c> leaf: server + receiver + a covenant-tweaked emulator
/// co-signer, pinned to <paramref name="SenderPkScript"/>, no timelock.
/// </summary>
/// <param name="SenderPkScript">Where the refund must pay — the sender's P2TR scriptPubKey (34 bytes).</param>
/// <param name="EmulatorPubKey">The emulator key the covenant tweaks.</param>
/// <remarks>
/// <para>
/// Every OTHER refund-side leaf requires the sender's own signature, so if the sender permanently
/// loses that key none of them are reachable. This leaf is the exception: it needs neither the
/// sender's presence nor their key, and the covenant is what still guarantees the payout can only
/// reach the sender's pre-committed address. It does need the receiver — deliberately, because that
/// is what lets server and receiver release the refund the moment they agree the swap failed,
/// rather than making the sender wait out the locktime.
/// </para>
/// <para>
/// <c>VHTLC.Options</c> also admits a timelocked twin of this leaf,
/// <c>nonInteractiveRefundWithoutReceiver</c> — the server plus the same covenant co-signer, after
/// the refund locktime, needing no participant signature at all. It is deliberately not modelled
/// here yet: it appends a ninth leaf and so moves the address, no corridor asks for it, and it lands
/// in its own change rather than riding along with this one.
/// </para>
/// </remarks>
public sealed record VHTLCv2NonInteractiveRefund(
    byte[] SenderPkScript,
    ECXOnlyPubKey EmulatorPubKey);
