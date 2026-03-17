using NBitcoin;

namespace NArk.Abstractions.Blockchain;

/// <summary>
/// Provides confirmed on-chain UTXOs and signing capability for fee funding.
/// Used by CPFP child transactions during unilateral exit broadcasting.
///
/// Host applications implement this to connect to their on-chain wallet:
/// - BTCPay Server: uses the store's on-chain wallet
/// - Standalone apps: uses a dedicated fee key/wallet
/// </summary>
public interface IFeeWallet
{
    /// <summary>
    /// Selects a confirmed on-chain UTXO with at least <paramref name="minAmount"/> value
    /// to fund CPFP child transaction fees.
    /// </summary>
    /// <returns>
    /// A fee coin with the UTXO details and signing key, or null if no suitable UTXO is available.
    /// </returns>
    Task<FeeCoin?> SelectFeeUtxoAsync(Money minAmount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a script for receiving change from CPFP child transactions.
    /// </summary>
    Task<Script> GetChangeScriptAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A confirmed on-chain UTXO that can be used to fund fees, along with its signing key.
/// </summary>
/// <param name="Outpoint">The UTXO outpoint.</param>
/// <param name="TxOut">The previous output being spent.</param>
/// <param name="SigningKey">The private key for P2TR keypath signing.</param>
public record FeeCoin(OutPoint Outpoint, TxOut TxOut, Key SigningKey)
{
    // Prevent accidental private key exposure via record ToString/logging
    public override string ToString() => $"FeeCoin {{ Outpoint = {Outpoint}, Amount = {TxOut.Value} }}";
}
