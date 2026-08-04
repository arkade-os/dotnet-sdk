using NArk.Abstractions;
using NArk.Core.Contracts;
using NArk.Transport.GrpcClient.Extensions;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Extensions;

public static class ArkCashExtensions
{
    public static ArkPaymentContract ToContract(this ArkCash cash, Network network)
    {
        var serverDesc = KeyExtensions
            .ParseOutputDescriptor(cash.ServerPubkey.ToBytes().ToHexStringLower(), network);
        var userDesc = KeyExtensions.ParseOutputDescriptor(cash.Pubkey.ToBytes().ToHexStringLower(), network);
        return new ArkPaymentContract(serverDesc, cash.LockTime, userDesc);
    }

    public static ArkAddress GetAddress(this ArkCash cash, Network network)
    {
        return cash.ToContract(network).GetArkAddress();
    }
}