using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;

namespace NArk.Core.Transformers;

public class PaymentContractTransformer(IWalletProvider walletProvider, ILogger<PaymentContractTransformer>? logger = null) : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not ArkPaymentContract paymentContract)
            return false;

        if (paymentContract.User is null)
            return false;

        if (await walletProvider.GetAddressProviderAsync(walletIdentifier) is not { } addressProvider)
            return false;

        if (!await addressProvider.IsOurs(paymentContract.User))
        {
            logger?.LogWarning(
                "PaymentContract user descriptor not ours: wallet={WalletId}, userDescriptor={UserDescriptor}",
                walletIdentifier, paymentContract.User);
            return false;
        }

        if (await walletProvider.GetSignerAsync(walletIdentifier) is null)
            return false;

        return true;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var paymentContract = (contract as ArkPaymentContract)!;
        return new ArkCoin(walletIdentifier, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, paymentContract.User ?? throw new InvalidOperationException("User is required for claim script generation"),
            paymentContract.CollaborativePath(), null, null, null, vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);
    }
}