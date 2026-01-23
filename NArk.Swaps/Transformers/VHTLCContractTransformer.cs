using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transformers;
using NBitcoin;

namespace NArk.Swaps.Transformers;

public class VHTLCContractTransformer(IWalletProvider walletProvider, IChainTimeProvider chainTimeProvider) : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not VHTLCContract htlc) return false;

        var addressProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier);

        if (htlc.Preimage is not null && await addressProvider!.IsOurs(htlc.Receiver))
        {
            return true;
        }

        var chainTime = await chainTimeProvider.GetChainTime();

        if (htlc.RefundLocktime.IsTimeLock &&
            htlc.RefundLocktime.Date < chainTime.Timestamp && await addressProvider!.IsOurs(htlc.Sender))
        {
            return true;
        }

        return false;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var htlc = contract as VHTLCContract;

        var addressProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier);

        if (htlc!.Preimage is not null && await addressProvider!.IsOurs(htlc.Receiver))
        {
            return new ArkCoin(walletIdentifier, htlc, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, htlc.Receiver,
                htlc.CreateClaimScript(), new WitScript(Op.GetPushOp(htlc.Preimage!)), null, null, vtxo.Swept);
        }

        var chainTime = await chainTimeProvider.GetChainTime();
        if (htlc.RefundLocktime.IsTimeLock &&
            htlc.RefundLocktime.Date < chainTime.Timestamp && await addressProvider!.IsOurs(htlc.Sender))
        {
            return new ArkCoin(walletIdentifier, htlc, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, htlc.Receiver,
                htlc.CreateRefundWithoutReceiverScript(), null, htlc.RefundLocktime, null, vtxo.Swept);
        }

        throw new InvalidOperationException("CanTransform should've return false for this coin");
    }
}