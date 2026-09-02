using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using Microsoft.Extensions.Logging;
using NArk.Arkade.Emulator;
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
    /// The claim and the two-party refund sit level, and only the solo refund gets headroom on top:
    /// the one leaf a funder can spend alone is the only one whose timing can take money from a
    /// claimant. The base cannot be a constant — the server rejects any script below its configured
    /// minimum, and that minimum spans orders of magnitude between deployments. Worse, the rejection
    /// lands only when a spend is attempted, so a wrong constant surfaces once money is already
    /// committed.
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

        var seconds = checked((uint)exit.LockPeriod.TotalSeconds);

        // Below the granularity the value is a block count by the SDK's own convention, not
        // seconds. Reading 144 blocks (~a day) as 144 seconds would round to a 512s timelock
        // against a day-long requirement — accepted at funding, refused at spend, money already
        // locked. The LockType check above catches an operator that says so; this catches one
        // whose seconds are too small to have been seconds.
        if (seconds < SwapScriptValues.SequenceGranularitySeconds)
        {
            throw new InvalidOperationException(
                $"the Arkade server's unilateral exit delay of {seconds} is below " +
                $"{SwapScriptValues.SequenceGranularitySeconds}s, which makes it a block count " +
                "rather than a number of seconds");
        }

        // BIP68 encodes at most 0xffff units of 512s, and the solo refund stacks its headroom on
        // top of the base — so the ceiling has to leave room for that, not merely for the base.
        var ceiling = 0xffff * SwapScriptValues.SequenceGranularitySeconds
                      - SwapScriptValues.SoloRefundHeadroomSeconds;
        if (seconds > ceiling)
        {
            throw new InvalidOperationException(
                $"the Arkade server's unilateral exit delay of {seconds}s exceeds what BIP68 can " +
                "encode once the solo refund's headroom is stacked above it");
        }

        // The three leaves time three DIFFERENT parties' recourse, so they are not interchangeable
        // rungs on a ladder:
        //
        //   unilateralClaim                  the receiver alone, holding the preimage
        //   unilateralRefund                 sender AND receiver — neither can spend it alone
        //   unilateralRefundWithoutReceiver  the sender alone, needing nobody
        //
        // Only the last is a solo path for the funder, so it is the only one whose timing can
        // steal: a funder able to refund before the claimant can claim takes money from someone
        // who holds the preimage and did nothing wrong. It therefore gets real headroom — sized
        // for what a claimant actually has to do with the server gone, which is an unroll
        // broadcast per chain step, each waiting on a confirmation, then the CSV spend.
        //
        // Claim sits level with the two-signature refund. Separating them bought nothing, since
        // neither party can spend that leaf alone, while spending headroom that does matter.
        var claim = SwapScriptValues.CeilToGranularity(seconds);
        return (claim, claim, claim + SwapScriptValues.SoloRefundHeadroomSeconds);
    }

    /// <summary>Accept an emulator key in either encoding and return its x-only form.</summary>
    /// <param name="pubkey">A 32-byte x-only key, or a 33-byte compressed one.</param>
    /// <returns>The x-only key the covenant commits to.</returns>
    /// <exception cref="ArgumentException">The bytes are not a key in either encoding.</exception>
    /// <remarks>
    /// Parsed, not sliced. Taking the first byte off any 33-byte blob yields 32 bytes shaped exactly
    /// like a key — the head of an uncompressed point, say — and the covenant would then commit to a
    /// co-signer nobody holds, in a script that is well formed and an address that looks ordinary.
    /// </remarks>
    public static ECXOnlyPubKey NormalizeToXOnly(byte[] pubkey)
    {
        if (pubkey.Length == 32)
        {
            return ECXOnlyPubKey.Create(pubkey);
        }
        if (pubkey.Length == 33)
        {
            return ECPubKey.TryCreate(pubkey, Context.Instance, out _, out var compressed)
                ? compressed.ToXOnlyPubKey()
                : throw new ArgumentException("not a valid compressed public key", nameof(pubkey));
        }
        throw new ArgumentException(
            $"expected a 32-byte x-only or 33-byte compressed public key, got {pubkey.Length} bytes",
            nameof(pubkey));
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
    /// Build both lockup shapes the covenant suite can take — without and with the timelocked
    /// refund leaf — from one shared parameter set.
    /// </summary>
    /// <param name="server">The Arkade server's key.</param>
    /// <param name="sender">The party that funds.</param>
    /// <param name="receiver">The party that claims.</param>
    /// <param name="hash">HASH160 of the preimage.</param>
    /// <param name="refundLocktime">When the sender's timelocked refund path opens.</param>
    /// <param name="unilateralClaimDelay">The claim CSV delay.</param>
    /// <param name="unilateralRefundDelay">The refund CSV delay.</param>
    /// <param name="unilateralRefundWithoutReceiverDelay">The refund-without-receiver CSV delay.</param>
    /// <param name="nonInteractiveClaim">The covenant claim leaf, identical in both shapes.</param>
    /// <param name="refundPkScript">Where both shapes' refund covenant must pay.</param>
    /// <param name="refundEmulatorPubKey">The emulator key both shapes' refund covenant tweaks.</param>
    /// <returns>
    /// The eight-leaf and nine-leaf contracts, differing from each other in nothing but that leaf.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Nothing on the wire says which shape a given solver has deployed, so a client cannot know in
    /// advance which to build. All three corridors derive both here and accept whichever matches the
    /// solver's quoted address. That stays safe because both shapes pin the refund covenant to the
    /// SAME destination as each other — whatever <paramref name="refundPkScript"/> names, which is
    /// the client's own address on the send and onchain-send corridors but the SOLVER's on the
    /// receive corridor. Either way the two candidates cannot disagree about it.
    /// </para>
    /// <para>
    /// The refund leaf is taken apart — destination and key, not a built
    /// <see cref="VHTLCv2NonInteractiveRefund"/> — precisely so there is no
    /// <see cref="VHTLCv2NonInteractiveRefund.WithoutReceiver"/> for this method to override and
    /// silently discard. A caller cannot pin the flag here, because pinning it is the one thing
    /// deriving both shapes exists to avoid.
    /// </para>
    /// </remarks>
    public static (VHTLCv2Contract EightLeaf, VHTLCv2Contract NineLeaf) DeriveBothLockupShapes(
        OutputDescriptor server,
        OutputDescriptor sender,
        OutputDescriptor receiver,
        uint160 hash,
        LockTime refundLocktime,
        Sequence unilateralClaimDelay,
        Sequence unilateralRefundDelay,
        Sequence unilateralRefundWithoutReceiverDelay,
        VHTLCv2NonInteractiveClaim nonInteractiveClaim,
        byte[] refundPkScript,
        ECXOnlyPubKey refundEmulatorPubKey)
    {
        VHTLCv2Contract Build(bool withoutReceiver) => new(
            server, sender, receiver, hash, refundLocktime,
            unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay,
            nonInteractiveClaim,
            new VHTLCv2NonInteractiveRefund(refundPkScript, refundEmulatorPubKey, withoutReceiver));

        return (Build(withoutReceiver: false), Build(withoutReceiver: true));
    }

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

        var contract = (VHTLCv2Contract)VHTLCv2Contract.Parse(entity.AdditionalData, network);

        // The parameters and the script are stored independently — the row's data builds the
        // covenant, the key it was filed under is the script we funded. So a parameter written
        // wrong, or dropped by a field-mapped backend, yields a contract that looks entirely
        // valid and simply cannot sign for the money. Comparing them here turns that into a
        // failure at load, rather than one discovered weeks later at a refund.
        var rebuilt = contract.GetScriptPubKey().ToHex();
        if (!string.Equals(rebuilt, swapPkScript, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"the stored lockup contract for swap '{swapId}' rebuilds to {rebuilt}, but it was " +
                $"filed under {swapPkScript} — these parameters are not this swap's");
        }

        return contract;
    }
}
