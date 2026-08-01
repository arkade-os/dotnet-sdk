using NBitcoin;

namespace NArk.Core.Contracts;

/// <summary>
/// Supplies the covenant-claim leaf of a <see cref="VHTLCContract"/> and authorises a
/// covenant signer to claim it — letting a swap complete while this wallet is offline.
/// </summary>
/// <remarks>
/// <para>
/// Declared here, implemented elsewhere. Computing the key requires the covenant
/// script dialect, which lives outside this package; keeping the abstraction in
/// <c>NArk.Core</c> lets swap code opt into covenant claims without taking a
/// dependency on whichever package implements them. Where no implementation is
/// registered, callers skip covenant claims entirely and behave exactly as before.
/// </para>
/// <para>
/// The two members are ordered by when they are needed, and the order is not
/// negotiable: <see cref="GetCovenantClaimKeyAsync"/> must run <em>before</em> the
/// contract is built, because the key changes its address, while
/// <see cref="RegisterAsync"/> runs after the counterparty has agreed to that address.
/// </para>
/// </remarks>
public interface ICovenantClaimProvider
{
    /// <summary>
    /// How long an authorisation from <see cref="RegisterAsync"/> stays valid.
    /// </summary>
    /// <remarks>
    /// Exposed so callers can pace renewals off the real value instead of duplicating
    /// the backend's constant. A registration may still be dropped early — a signer
    /// that restarts loses everything it was holding — so this is an upper bound, not
    /// a guarantee.
    /// </remarks>
    TimeSpan RegistrationLifetime { get; }

    /// <summary>
    /// Returns the co-signer key for a covenant-claim leaf that may only pay
    /// <paramref name="claimDestination"/>, ready to pass as
    /// <see cref="VHTLCContract.CovenantClaimKey"/>.
    /// </summary>
    /// <remarks>
    /// The returned key is bound to the destination, so a signer holding it cannot
    /// redirect the funds. Callers must use the value verbatim — deriving or
    /// substituting a key by hand yields an address the counterparty will not fund.
    /// </remarks>
    /// <param name="claimDestination">
    /// P2TR scriptPubKey the claim must pay. Should be a script this wallet already
    /// watches, or the claimed funds land somewhere it never scans.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    Task<TaprootPubKey> GetCovenantClaimKeyAsync(
        Script claimDestination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authorises the covenant signer to claim <paramref name="swapAddress"/> on this
    /// wallet's behalf once it is funded.
    /// </summary>
    /// <remarks>
    /// This hands the preimage to the signer, so call it only for a contract whose
    /// covenant leaf was built from <see cref="GetCovenantClaimKeyAsync"/> with the
    /// same <paramref name="claimDestination"/> — the authorisation is worthless
    /// otherwise, and the signer will reject it.
    /// <para>
    /// An authorisation may be short-lived and is not guaranteed to survive a signer
    /// restart, so callers that need cover for the whole lifetime of a swap should
    /// re-register periodically rather than treat this as fire-and-forget. Repeat
    /// calls for the same address are safe.
    /// </para>
    /// </remarks>
    /// <param name="swapAddress">bech32m Arkade address of the contract to be claimed.</param>
    /// <param name="preimage">The 32-byte preimage unlocking the hashlock.</param>
    /// <param name="claimDestination">The destination the covenant key was bound to.</param>
    /// <param name="taptree">The contract's tapscript leaves, including the covenant leaf.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    Task RegisterAsync(
        string swapAddress,
        byte[] preimage,
        Script claimDestination,
        TapScript[] taptree,
        CancellationToken cancellationToken = default);
}
