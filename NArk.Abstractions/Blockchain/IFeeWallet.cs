using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Blockchain;

/// <summary>
/// Provides confirmed on-chain UTXOs and signing capability for fee funding.
/// Used by CPFP child transactions during unilateral exit broadcasting.
///
/// Host applications implement this to connect to their on-chain wallet:
/// - BTCPay Server: uses the store's on-chain wallet (signs internally)
/// - HSM / hardware-wallet integrations: never expose raw keys
/// - Standalone apps with an in-memory key: see TestFeeWallet for the pattern
///
/// <para>
/// The contract is sighash-callback shaped (mirroring
/// <see cref="Wallets.IArkadeWalletSigner.Sign"/>) — the SDK never sees the
/// underlying signing material. Implementations can sign with a raw <c>Key</c>,
/// proxy to a hardware wallet, call out to a remote signer, or delegate to
/// BTCPay's own wallet manager; the choice is opaque to the SDK.
/// </para>
/// </summary>
public interface IFeeWallet
{
    /// <summary>
    /// Selects a confirmed on-chain UTXO with at least <paramref name="minAmount"/> value
    /// to fund CPFP child transaction fees. Returns the UTXO without any signing material;
    /// signing is requested separately via <see cref="SignFeeUtxoAsync"/>.
    /// </summary>
    /// <returns>
    /// A fee UTXO descriptor, or null if no suitable UTXO is available.
    /// </returns>
    Task<FeeUtxo?> SelectFeeUtxoAsync(Money minAmount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs a P2TR keypath spend of a previously-selected fee UTXO.
    /// The SDK computes the sighash (using the standard taproot algorithm with
    /// all prevouts) and asks the wallet to return a Schnorr signature over it —
    /// without ever requiring the wallet to surface its private key.
    /// </summary>
    /// <param name="feeOutpoint">
    /// The fee UTXO being spent. Used by wallets that hold multiple keys to
    /// disambiguate which signing material to use.
    /// </param>
    /// <param name="sighash">Precomputed taproot sighash.</param>
    /// <param name="sighashType">Sighash type (typically <see cref="TaprootSigHash.Default"/>).</param>
    Task<SecpSchnorrSignature> SignFeeUtxoAsync(
        OutPoint feeOutpoint,
        uint256 sighash,
        TaprootSigHash sighashType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a script for receiving change from CPFP child transactions.
    /// </summary>
    Task<Script> GetChangeScriptAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A confirmed on-chain UTXO selected for fee funding. Carries only the
/// outpoint and the previous output — no signing material. The wallet that
/// returned it is the only party that can produce a valid signature for it,
/// via <see cref="IFeeWallet.SignFeeUtxoAsync"/>.
/// </summary>
/// <param name="Outpoint">The UTXO outpoint.</param>
/// <param name="TxOut">The previous output being spent (script + amount).</param>
public record FeeUtxo(OutPoint Outpoint, TxOut TxOut);
