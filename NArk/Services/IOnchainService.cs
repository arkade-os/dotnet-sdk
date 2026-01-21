using NArk.Abstractions;


namespace NArk.Services;

public interface IOnchainService
{
    Task<Guid> InitiateCollaborativeExit(string walletId, ArkTxOut output,
        CancellationToken cancellationToken = default);
    Task<Guid> InitiateCollaborativeExit(ArkCoin[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
}