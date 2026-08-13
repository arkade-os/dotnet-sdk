using NBitcoin;
namespace NArk.ArkadeIntents.Lightning;

/// <summary>
/// The conversions a swap's script parameters have to go through before they can be committed to.
/// </summary>
/// <remarks>
/// Small, but each one is a place where a plausible-looking value is silently the wrong one — a
/// locktime a verifier reads as a block height, a delay BIP68 rounds down, or the invoice's hash
/// used where the script commits to a different digest of it.
/// </remarks>
public static class SwapScriptValues
{
    /// <summary>BIP65: at or above this value a locktime is a unix timestamp rather than a block height.</summary>
    public const uint LocktimeThreshold = 500_000_000;

    /// <summary>BIP68 encodes relative time in units of 512 seconds.</summary>
    public const uint SequenceGranularitySeconds = 512;

    /// <summary>
    /// How far the funder's solo refund opens after the claim, in seconds.
    /// </summary>
    /// <remarks>
    /// The one leaf a funder can spend without anybody else, so the only one whose timing can take
    /// money from a claimant who holds the preimage and did nothing wrong. Sized for what reaching
    /// the claim actually costs with the Arkade server gone — an unroll broadcast per chain step,
    /// each waiting on a confirmation, then the CSV spend. A single granularity tick never covered
    /// that. Must match the reference solver's <c>SOLO_REFUND_HEADROOM_SECONDS</c>, and is a whole
    /// number of BIP68 units so that stacking it cannot silently re-round.
    /// </remarks>
    public const uint SoloRefundHeadroomSeconds = 8 * SequenceGranularitySeconds;

    /// <summary>Round a duration up to the next whole BIP68 512-second unit.</summary>
    /// <param name="seconds">The duration to round, in seconds.</param>
    /// <returns>The smallest multiple of 512 greater than or equal to <paramref name="seconds"/>.</returns>
    /// <remarks>
    /// Up, never down: rounding a required delay down produces a script the server accepts at
    /// funding and rejects at spend, which surfaces only once there is money behind it.
    /// </remarks>
    public static uint CeilToGranularity(uint seconds) =>
        (seconds + SequenceGranularitySeconds - 1) / SequenceGranularitySeconds * SequenceGranularitySeconds;

    /// <summary>
    /// Bridge a BOLT11 payment hash to the 20-byte hash the script commits to: the invoice carries
    /// <c>sha256(P)</c>, the script's HASH160 branch commits to <c>ripemd160(sha256(P))</c>.
    /// </summary>
    /// <param name="paymentHash">The invoice's 32-byte payment hash.</param>
    /// <returns>The 20-byte HASH160 the covenant script commits to.</returns>
    /// <exception cref="ArgumentException">The payment hash is not 32 bytes.</exception>
    /// <remarks>
    /// This is why the maker never needs to see the preimage: it can commit to the hash of a secret
    /// it does not hold, and paying the invoice is what reveals that secret to whoever claims.
    /// </remarks>
    /// <summary>
    /// The script's 20-byte hash, taken straight from a decoded invoice.
    /// </summary>
    /// <param name="paymentHash">The invoice's payment hash.</param>
    /// <returns>RIPEMD160 of the payment hash, big-endian.</returns>
    /// <remarks>
    /// Always prefer this over the byte-array overload when the value came from a BOLT11.
    /// <see cref="uint256"/> hands out its bytes little-endian by default, which is the opposite of
    /// how a payment hash is written, hashed and agreed upon — and reversing it produces a perfectly
    /// well-formed script committing to a hash no preimage will ever open. Nothing rejects that
    /// locally: it surfaces as a lockup address the counterparty does not recognise, and would
    /// surface as unspendable funds if anything ever funded it.
    /// </remarks>
    public static byte[] PreimageHashFromPaymentHash(uint256 paymentHash) =>
        PreimageHashFromPaymentHash(paymentHash.ToBytes(lendian: false));

    /// <param name="paymentHash">The payment hash, big-endian — as written, not as
    /// <see cref="uint256.ToBytes(bool)"/> defaults to.</param>
    public static byte[] PreimageHashFromPaymentHash(byte[] paymentHash)
    {
        if (paymentHash.Length != 32)
        {
            throw new ArgumentException(
                $"payment hash must be 32 bytes, got {paymentHash.Length}", nameof(paymentHash));
        }
        return NBitcoin.Crypto.Hashes.RIPEMD160(paymentHash, paymentHash.Length);
    }
}
