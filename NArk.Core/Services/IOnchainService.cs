using NArk.Abstractions;


namespace NArk.Core.Services;

public interface IOnchainService
{
    Task<string> InitiateCollaborativeExit(string walletId, ArkTxOut output,
        CancellationToken cancellationToken = default);
    Task<string> InitiateCollaborativeExit(ArkCoin[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
}