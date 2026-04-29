#nullable enable
using NBitcoin;

namespace NBitcoin.Scripting
{
    /// <summary>
    /// Compatibility shims so the vendored pre-10.x OutputDescriptor code builds
    /// against NBitcoin 10, which renamed <c>PubKey.TaprootPubKey</c> to
    /// <c>PubKey.TaprootInternalKey</c> and made some internals inaccessible.
    /// </summary>
    internal static class NBitcoinCompat
    {
#if HAS_SPAN
        public static TaprootPubKey GetTaprootPubKey(this PubKey pubkey)
            => new TaprootPubKey(pubkey.TaprootInternalKey.ToBytes());
#endif
    }
}
