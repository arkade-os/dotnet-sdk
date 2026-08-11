using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.Core;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Lightning;

/// <summary>
/// What both Lightning corridors need identically: the CSV ladder, the key conversions, and
/// rebuilding a funded lockup.
/// </summary>
/// <remarks>
/// The two corridors differ only in who occupies which covenant role. Everything feeding that
/// construction is the same on both sides, and it must stay the same — the delays especially, since
/// two clients deriving them differently would derive two different addresses for the same swap.
/// Shared here rather than mirrored, so there is one place to be right.
/// </remarks>
public static class LightningCorridor
{
    /// <summary>The three CSV delays the covenant's timelocked leaves use.</summary>
    /// <param name="serverInfo">The Arkade server's advertised terms.</param>
    /// <returns>The claim, refund and refund-without-receiver delays, in seconds.</returns>
    /// <exception cref="InvalidOperationException">The server denominates its exit delay in blocks.</exception>
    /// <remarks>
    /// <para>
    /// Deliberately absent from the RFQ wire. Both sides read the same public <c>/v1/info</c> and
    /// apply the same rule, so they reach identical numbers without the solver being able to
    /// influence them — a delay it could dictate would be a delay it could stretch.
    /// </para>
    /// <para>
    /// Each rung sits one BIP68 unit above the last, which is what keeps the ladder ordered: the
    /// receiver's claim opens first, then the two-party refund, then the recourse that needs nobody.
    /// The base cannot be a constant — the server rejects any script below its configured minimum,
    /// and that minimum spans orders of magnitude between deployments. Worse, the rejection lands
    /// only when a spend is attempted, so a wrong constant surfaces once money is already committed.
    /// </para>
    /// </remarks>
    public static (uint Claim, uint Refund, uint RefundWithoutReceiver) UnilateralDelays(
        ArkServerInfo serverInfo)
    {
        var exit = serverInfo.UnilateralExit;
        if (exit.LockType != SequenceLockType.Time)
        {
            throw new InvalidOperationException(
                "the Arkade server advertises its unilateral exit delay in blocks; this swap script " +
                "encodes a time-based delay, and block-interval variance is far too wide to hold a " +
                "Lightning HTLC deadline against");
        }

        var claim = SwapScriptValues.CeilToGranularity(checked((uint)exit.LockPeriod.TotalSeconds));
        return (claim,
            claim + SwapScriptValues.SequenceGranularitySeconds,
            claim + 2 * SwapScriptValues.SequenceGranularitySeconds);
    }

    /// <summary>Accept an emulator key in either encoding and return its x-only form.</summary>
    /// <param name="pubkey">A 32-byte x-only key, or a 33-byte compressed one.</param>
    /// <returns>The x-only key the covenant commits to.</returns>
    /// <exception cref="ArgumentException">The bytes are not a key in either encoding.</exception>
    /// <remarks>
    /// Parses a 33-byte input rather than trusting its length. Slicing the first byte off any 33-byte
    /// blob yields 32 bytes that look like a key and are not one — the first 33 bytes of an
    /// uncompressed point, say — and the covenant would then commit to a co-signer nobody holds.
    /// Nothing downstream can notice: the script is well formed and the address is ordinary. On these
    /// corridors the address check catches it before funding, but that is a second line, not this
    /// one's job, and the asset corridor has no counterparty address to compare against at all.
    /// </remarks>
    public static ECXOnlyPubKey NormalizeToXOnly(byte[] pubkey)
    {
        if (pubkey.Length == 32) return ECXOnlyPubKey.Create(pubkey);
        if (pubkey.Length == 33 && ECPubKey.TryCreate(pubkey, Context.Instance, out _, out var compressed))
        {
            return compressed.ToXOnlyPubKey();
        }
        throw new ArgumentException(
            $"expected a 32-byte x-only or 33-byte compressed public key, got {pubkey.Length} bytes" +
            (pubkey.Length == 33 ? " that do not parse as one" : ""), nameof(pubkey));
    }

    /// <summary>Wrap a counterparty's x-only key as a descriptor.</summary>
    /// <param name="xOnlyHex">The 32-byte key, hex.</param>
    /// <param name="network">The network the descriptor belongs to.</param>
    /// <returns>A descriptor carrying that key.</returns>
    /// <remarks>
    /// The parity byte is arbitrary — every leaf commits to the x-only form — so the even prefix is
    /// as good as any.
    /// </remarks>
    public static OutputDescriptor DescriptorForXOnly(string xOnlyHex, Network network) =>
        KeyExtensions.ParseOutputDescriptor("02" + xOnlyHex.ToLowerInvariant(), network);

    /// <summary>
    /// Rebuild a funded lockup from the contract imported before it was funded.
    /// </summary>
    /// <param name="contractStorage">Where the lockup was imported.</param>
    /// <param name="swapPkScript">The lockup's scriptPubKey, hex.</param>
    /// <param name="swapId">The swap's id, for the error message.</param>
    /// <param name="network">The network the descriptors belong to.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The reconstructed contract.</returns>
    /// <exception cref="InvalidOperationException">The contract is not in the store.</exception>
    /// <remarks>
    /// The store is the only record of how a funded script was built, which is why both corridors
    /// import before they commit anything — money in a script nobody can rebuild is money nobody can
    /// spend.
    /// </remarks>
    public static async Task<VHTLCv2Contract> LoadLockupAsync(
        IContractStorage contractStorage,
        string swapPkScript,
        string swapId,
        Network network,
        CancellationToken cancellationToken = default)
    {
        var contracts = await contractStorage.GetContracts(
            scripts: [swapPkScript], cancellationToken: cancellationToken);
        var entity = contracts.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"the lockup contract for swap '{swapId}' is not in the contract store — " +
                "without it the funded script cannot be rebuilt");

        return (VHTLCv2Contract)VHTLCv2Contract.Parse(entity.AdditionalData, network);
    }
}
