using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NBitcoin;

namespace NArk.Core.Transformers;

public class HashLockedContractTransformer(IWalletProvider walletProvider) : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not HashLockedArkPaymentContract hashLockedArkPaymentContract)
            return false;

        if (await walletProvider.GetAddressProviderAsync(walletIdentifier) is not { } addressProvider)
            return false;

        if (!await addressProvider.IsOurs(hashLockedArkPaymentContract.User))
            return false;

        if (await walletProvider.GetSignerAsync(walletIdentifier) is not { } signer)
            return false;

        return true;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var contractObj = contract as HashLockedArkPaymentContract;
        return new ArkCoin(walletIdentifier, contractObj!, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, contractObj!.User ?? throw new InvalidOperationException("User is required for claim script generation"),
            contractObj!.CreateClaimScript(), new WitScript(Op.GetPushOp(contractObj.Preimage)), null, null, vtxo.Swept);
    }
}