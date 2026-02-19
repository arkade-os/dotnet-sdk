using NArk.Abstractions;


namespace NArk.Core.Services;

/// <summary>
/// Service for moving funds from Ark back to the Bitcoin base layer via collaborative exits.
/// </summary>
public interface IOnchainService
{
    /// <summary>
    /// Initiates a collaborative exit for the wallet, sending funds to a single on-chain output.
    /// Returns the Bitcoin transaction ID.
    /// </summary>
    Task<string> InitiateCollaborativeExit(string walletId, ArkTxOut output,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Initiates a collaborative exit with explicit coin inputs and multiple on-chain outputs.
    /// Returns the Bitcoin transaction ID.
    /// </summary>
    Task<string> InitiateCollaborativeExit(ArkCoin[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
}