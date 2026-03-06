using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transformers;
using NBitcoin;

namespace NArk.Swaps.Transformers;

public class VHTLCContractTransformer(IWalletProvider walletProvider, IChainTimeProvider chainTimeProvider, ILogger<VHTLCContractTransformer>? logger = null) : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not VHTLCContract htlc) return false;

        var addressProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier);

        if (htlc.Preimage is not null && await addressProvider!.IsOurs(htlc.Receiver))
        {
            return await walletProvider.GetSignerAsync(walletIdentifier) is not null;
        }

        var chainTime = await chainTimeProvider.GetChainTime();

        if (htlc.RefundLocktime.IsTimeLock &&
            htlc.RefundLocktime.Date < chainTime.Timestamp && await addressProvider!.IsOurs(htlc.Sender))
        {
            return await walletProvider.GetSignerAsync(walletIdentifier) is not null;
        }

        return false;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var htlc = contract as VHTLCContract;

        var addressProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier);

        if (htlc!.Preimage is not null && await addressProvider!.IsOurs(htlc.Receiver))
        {
            logger?.LogInformation("VHTLC claim: wallet={WalletId}, receiver={Receiver}, sender={Sender}, outpoint={Outpoint}",
                walletIdentifier, htlc.Receiver, htlc.Sender, vtxo.OutPoint);
            return new ArkCoin(walletIdentifier, htlc, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, htlc.Receiver,
                htlc.CreateClaimScript(), new WitScript(Op.GetPushOp(htlc.Preimage!)), null, null, vtxo.Swept, vtxo.Unrolled);
        }

        var chainTime = await chainTimeProvider.GetChainTime();
        if (htlc.RefundLocktime.IsTimeLock &&
            htlc.RefundLocktime.Date < chainTime.Timestamp && await addressProvider!.IsOurs(htlc.Sender))
        {
            logger?.LogInformation("VHTLC refund: wallet={WalletId}, sender={Sender}, receiver={Receiver}, outpoint={Outpoint}, refundLocktime={RefundLocktime}",
                walletIdentifier, htlc.Sender, htlc.Receiver, vtxo.OutPoint, htlc.RefundLocktime);
            return new ArkCoin(walletIdentifier, htlc, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, htlc.Sender,
                htlc.CreateRefundWithoutReceiverScript(), null, htlc.RefundLocktime, null, vtxo.Swept, vtxo.Unrolled);
        }

        throw new InvalidOperationException("CanTransform should've return false for this coin");
    }
}