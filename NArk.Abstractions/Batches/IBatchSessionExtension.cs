using NBitcoin;

namespace NArk.Abstractions.Batches;

/// <summary>
/// Plug-point invoked by <c>BatchSession</c> at PSBT-emitting points in the
/// batch flow. Lets extensions co-sign / mutate the PSBTs without
/// <c>BatchSession</c> needing to know about specific co-signers (introspector,
/// future custody services, etc.).
/// </summary>
/// <remarks>
/// <para>
/// Extensions are stateless across batches — they receive the current
/// spending coins on every call so they can decide per-batch whether to
/// engage (most often "are any of these coins arkade-bound?").
/// </para>
/// <para>
/// Registered extensions are invoked in DI registration order. An extension
/// that has nothing to do for a given batch should return the input PSBTs
/// unchanged from <see cref="CoSignAsync"/> (and <c>false</c> from
/// <see cref="ShouldHandleAsync"/> if the whole batch is irrelevant — that
/// short-circuits per-phase calls).
/// </para>
/// </remarks>
public interface IBatchSessionExtension
{
    /// <summary>
    /// Cheap "is this batch relevant to me?" check, called once at batch
    /// session initialization. <c>BatchSession</c> skips invoking
    /// <see cref="CoSignAsync"/> on this extension for the rest of the
    /// batch when this returns <c>false</c>.
    /// </summary>
    Task<bool> ShouldHandleAsync(IReadOnlyList<ArkCoin> spendingCoins, CancellationToken cancellationToken);

    /// <summary>
    /// Co-sign / mutate the supplied PSBTs at the given phase of the batch
    /// flow. The returned list replaces the input (same length, same order);
    /// extensions can pass through unchanged when there's nothing to do.
    /// </summary>
    Task<IReadOnlyList<PSBT>> CoSignAsync(
        BatchExtensionPhase phase,
        IReadOnlyList<PSBT> psbts,
        IReadOnlyList<ArkCoin> spendingCoins,
        CancellationToken cancellationToken);
}

/// <summary>
/// The point in the batch flow at which an <see cref="IBatchSessionExtension"/>
/// is being invoked.
/// </summary>
public enum BatchExtensionPhase
{
    /// <summary>
    /// After tree signing aggregates a partially-signed Ark tx tree, before
    /// the signatures are submitted upstream. Extension co-signs leaves whose
    /// inputs need additional signatures (introspector for arkade-bound
    /// inputs, etc.).
    /// </summary>
    PostTreeSigning = 1,

    /// <summary>
    /// Before forfeit-tx submission and commitment-tx finalisation. Extension
    /// co-signs forfeits and (optionally) the commitment for inputs it owns.
    /// </summary>
    PreForfeitFinalization = 2,
}
