using NArk.Abstractions;
using NArk.Core.Contracts;
using NArk.Transport.GrpcClient.Extensions;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Extensions;

/// <summary>
/// Extension methods that turn an <see cref="ArkadeCash"/> bearer instrument into the
/// Arkade contract and address its funds live at.
/// </summary>
public static class ArkadeCashExtensions
{
    /// <summary>Rebuilds the Arkade payment contract the instrument's funds are locked to.</summary>
    /// <param name="cash">The bearer instrument.</param>
    /// <param name="network">The network whose key encoding is used to build the descriptors.</param>
    /// <returns>The payment contract owned by the instrument's key.</returns>
    public static ArkPaymentContract ToContract(this ArkadeCash cash, Network network)
    {
        var serverDesc = KeyExtensions
            .ParseOutputDescriptor(cash.ServerPubkey.ToBytes().ToHexStringLower(), network);
        var userDesc = KeyExtensions.ParseOutputDescriptor(cash.Pubkey.ToBytes().ToHexStringLower(), network);
        return new ArkPaymentContract(serverDesc, cash.LockTime, userDesc);
    }

    /// <summary>Derives the Arkade address the instrument is funded at.</summary>
    /// <param name="cash">The bearer instrument.</param>
    /// <param name="network">The network whose key encoding is used to build the descriptors.</param>
    /// <returns>The Arkade address of the instrument's payment contract.</returns>
    public static ArkAddress GetAddress(this ArkadeCash cash, Network network)
    {
        return cash.ToContract(network).GetArkAddress();
    }
}