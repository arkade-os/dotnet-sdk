using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;

namespace NArk.Core.Transformers;

public class BoardingContractTransformer(IWalletProvider walletProvider, ILogger<BoardingContractTransformer>? logger = null) : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not ArkBoardingContract boardingContract)
            return false;

        if (boardingContract.User is null)
            return false;

        if (await walletProvider.GetAddressProviderAsync(walletIdentifier) is not { } addressProvider)
            return false;

        if (!await addressProvider.IsOurs(boardingContract.User))
        {
            logger?.LogWarning(
                "BoardingContract user descriptor not ours: wallet={WalletId}, userDescriptor={UserDescriptor}",
                walletIdentifier, boardingContract.User);
            return false;
        }

        if (await walletProvider.GetSignerAsync(walletIdentifier) is null)
            return false;

        return true;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var boardingContract = (contract as ArkBoardingContract)!;
        return new ArkCoin(walletIdentifier, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, boardingContract.User ?? throw new InvalidOperationException("User is required for claim script generation"),
            boardingContract.CollaborativePath(), null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);
    }
}
