using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;

namespace NArk.Transport.GrpcClient.Extensions;

internal static class KeyExtensions
{
    public static OutputDescriptor ParseOutputDescriptor(string str, Network network)
    {
        if (!HexEncoder.IsWellFormed(str))
            return OutputDescriptor.Parse(str, network);

        var bytes = Convert.FromHexString(str);
        if (bytes.Length != 32 && bytes.Length != 33)
        {
            throw new ArgumentException("the string must be 32/33 bytes long", nameof(str));
        }

        return OutputDescriptor.Parse($"tr({str})", network);
    }
}