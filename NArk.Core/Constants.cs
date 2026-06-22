using NBitcoin;

namespace NArk.Core;

internal class Constants
{
    internal static readonly string UnspendableKeyHex =
        "0250929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0";

    internal static readonly byte[] UnspendableKey = Convert.FromHexString(UnspendableKeyHex);

    // OP_1 PUSH2 "Ns" Arkade protocol P2A anchor marker (BIP 431 variant)
    internal static readonly Script ArkP2A = Script.FromHex("51024e73");
}