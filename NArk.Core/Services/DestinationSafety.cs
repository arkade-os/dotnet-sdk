using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NBitcoin.Secp256k1;

namespace NArk.Core.Services;

/// <summary>Detects when a wallet's sweep destination has been orphaned by an Arkade signer rotation.</summary>
public static class DestinationSafety
{
    /// <summary>Metadata key marking a destination disabled pending user re-confirmation. Value = deprecated server key (hex).</summary>
    public const string PendingConfirmationMetadataKey = "destination:pendingConfirmation";

    /// <summary>
    /// True when <paramref name="destination"/> points at a server signer key that was ours and is now
    /// deprecated (literally targets the rotated-away signer). External keys never in our deprecated
    /// set, and the current signer, are not stale.
    /// </summary>
    public static bool IsStale(ArkAddress? destination, ArkServerInfo serverInfo)
    {
        if (destination is null || serverInfo.DeprecatedSigners.Count == 0) return false;
        foreach (var deprecated in serverInfo.DeprecatedSigners.Keys)
            if (ECXOnlyPubKeyComparer.Instance.Equals(deprecated, destination.ServerKey)) return true;
        return false;
    }
}
