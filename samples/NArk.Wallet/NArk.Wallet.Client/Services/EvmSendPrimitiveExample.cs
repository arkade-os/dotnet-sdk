using System.Numerics;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.Core;
using NBitcoin.Scripting;

namespace NArk.Wallet.Client.Services;

/// <summary>Shows the validation boundary a wallet must cross before funding an EVM send.</summary>
public static class EvmSendPrimitiveExample
{
    /// <summary>Requests and locally validates a current EVM send quote without funding it.</summary>
    /// <returns>The ABI tuple and exact locally derived lockup.</returns>
    public static async Task<(Erc20SwapValues Swap, EvmArkadeLockupValidation Lockup)> PrepareAsync(
        IRfqTransport rfq,
        EvmSendRfqRequest request,
        EvmSendPolicy policy,
        long now,
        BigInteger evmTip,
        ArkServerInfo serverInfo,
        OutputDescriptor clientRefund,
        byte[] refundPkScript,
        string emulatorPubkey,
        CancellationToken cancellationToken = default)
    {
        var quote = await rfq.RequestEvmSendQuoteAsync(request, cancellationToken);
        var swap = EvmSendQuoteValidator.Validate(request, quote, policy, now, evmTip);
        var lockup = EvmArkadeLockupValidator.Validate(
            request, quote, policy, serverInfo, clientRefund, refundPkScript, emulatorPubkey);
        return (swap, lockup);
    }
}
