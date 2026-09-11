using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Core.Contracts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Extensions;

/// <summary>
/// Extension methods that turn an <see cref="ArkadeCash"/> bearer instrument into the
/// Arkade contract and address its funds live at.
/// </summary>
public static class ArkadeCashExtensions
{
    /// <summary>
    /// Rebuilds the Arkade payment contract the instrument's funds are locked to, reusing the
    /// server key descriptor reported by the Arkade server.
    /// </summary>
    /// <param name="cash">The bearer instrument.</param>
    /// <param name="serverInfo">Info from the Arkade server the note was issued against.</param>
    /// <returns>The payment contract owned by the instrument's key.</returns>
    /// <exception cref="InvalidOperationException">
    /// The note names a different signer key than the server reports, so its funds are not
    /// claimable against this server.
    /// </exception>
    /// <remarks>
    /// Prefer this overload over <see cref="ToContract(ArkadeCash, Network)"/> whenever server
    /// info is at hand. A note only carries the server's 32-byte x-only key, so rebuilding the
    /// descriptor from it yields <c>tr(&lt;x-only&gt;)</c>, while the server may report a 33-byte
    /// compressed key. Both derive the same address, but the descriptors are not equal, and
    /// contract import rejects a server key that does not match the one it knows.
    /// </remarks>
    public static ArkPaymentContract ToContract(this ArkadeCash cash, ArkServerInfo serverInfo)
    {
        if (serverInfo.SignerKey.ToXOnlyPubKey() != cash.ServerPubkey)
        {
            throw new InvalidOperationException(
                "ArkadeCash was issued against a different Arkade server key than the one this server reports.");
        }

        var userDesc = KeyExtensions.ParseOutputDescriptor(cash.Pubkey.ToBytes().ToHexStringLower(), serverInfo.Network);
        return new ArkPaymentContract(serverInfo.SignerKey, cash.LockTime, userDesc);
    }

    /// <summary>
    /// Rebuilds the Arkade payment contract the instrument's funds are locked to, using only what
    /// the note itself carries.
    /// </summary>
    /// <param name="cash">The bearer instrument.</param>
    /// <param name="network">The network whose key encoding is used to build the descriptors.</param>
    /// <returns>The payment contract owned by the instrument's key.</returns>
    /// <remarks>
    /// The server descriptor is rebuilt from the note's x-only key, which is enough to derive the
    /// address but may not equal the descriptor the server reports. Use
    /// <see cref="ToContract(ArkadeCash, ArkServerInfo)"/> when importing the contract into a wallet.
    /// </remarks>
    public static ArkPaymentContract ToContract(this ArkadeCash cash, Network network)
    {
        var serverDesc = KeyExtensions.ParseOutputDescriptor(cash.ServerPubkey.ToBytes().ToHexStringLower(), network);
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
