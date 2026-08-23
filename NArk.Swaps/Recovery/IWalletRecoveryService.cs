using NArk.Abstractions.Recovery;

namespace NArk.Swaps.Recovery;

/// <summary>
/// Wallet-type-agnostic recovery entry point. Given a wallet id, rebuilds local
/// state from on-chain / indexer / boltz sources: contracts (incl. legacy script
/// variants under deprecated server signers), the HD derivation index, funds
/// (VTXOs), boltz swap data, and any in-flight Ark transactions.
/// <para>
/// Dispatches by wallet type: HD wallets get a gap-limit index scan (which also
/// restores boltz swaps via the discovery providers); SingleKey wallets — whose
/// contract set is fixed by their one key — re-derive that contract and restore
/// swaps directly. Both then finalize pending transactions and sync funds.
/// </para>
/// </summary>
public interface IWalletRecoveryService
{
    /// <summary>Recover everything reconstructable for <paramref name="walletId"/>.</summary>
    /// <param name="walletId">The wallet to recover (must already exist in storage).</param>
    /// <param name="options">HD scan tuning (gap limit, max index). Ignored for SingleKey.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<WalletRecoveryReport> RecoverAsync(
        string walletId,
        RecoveryOptions? options = null,
        CancellationToken cancellationToken = default);
}
