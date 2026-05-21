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

public class VHTLCContractTransformer(IWalletProvider walletProvider, IBitcoinBlockchain chainTimeProvider, ILogger<VHTLCContractTransformer>? logger = null) : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not VHTLCContract htlc) return false;

        // TEMP latency probe.
        var swAddr = System.Diagnostics.Stopwatch.StartNew();
        var addressProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier);
        var addrMs = swAddr.ElapsedMilliseconds;

        if (htlc.Preimage is not null)
        {
            var swIsOurs = System.Diagnostics.Stopwatch.StartNew();
            var isOurs = await addressProvider!.IsOurs(htlc.Receiver);
            var isOursMs = swIsOurs.ElapsedMilliseconds;
            if (isOurs)
            {
                var swSigner = System.Diagnostics.Stopwatch.StartNew();
                var signer = await walletProvider.GetSignerAsync(walletIdentifier);
                logger?.LogTrace(
                    "[vhtlc-probe] CanTransform (claim path): GetAddressProvider={AddrMs}ms IsOurs={IsOursMs}ms GetSigner={SignerMs}ms",
                    addrMs, isOursMs, swSigner.ElapsedMilliseconds);
                return signer is not null;
            }
        }

        var swChainTime = System.Diagnostics.Stopwatch.StartNew();
        var chainTime = await chainTimeProvider.GetChainTime();
        var chainMs = swChainTime.ElapsedMilliseconds;

        if (htlc.RefundLocktime.IsTimeLock &&
            htlc.RefundLocktime.Date < chainTime.Timestamp && await addressProvider!.IsOurs(htlc.Sender))
        {
            logger?.LogTrace(
                "[vhtlc-probe] CanTransform (refund path): GetAddressProvider={AddrMs}ms GetChainTime={ChainMs}ms",
                addrMs, chainMs);
            return await walletProvider.GetSignerAsync(walletIdentifier) is not null;
        }

        logger?.LogTrace(
            "[vhtlc-probe] CanTransform (neither): GetAddressProvider={AddrMs}ms GetChainTime={ChainMs}ms",
            addrMs, chainMs);
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