using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Recovery;
using NArk.Core.Transport;

namespace NArk.ArkadeIntents.Services;

/// <summary>Who took a spent HTLC-corridor lockup, decided only from what the chain can prove.</summary>
internal static class SwapSpendVerdict
{
    public static bool IsHtlcCorridor(ArkadeSwapIntentType type) =>
        type is ArkadeSwapIntentType.BtcToLightning or ArkadeSwapIntentType.LightningToBtc
            or ArkadeSwapIntentType.BtcToOnchain or ArkadeSwapIntentType.OnchainToBtc;

    public static bool IsSend(ArkadeSwapIntentType type) =>
        type is ArkadeSwapIntentType.BtcToLightning or ArkadeSwapIntentType.BtcToOnchain;

    /// <returns>True for a proven claim, false for a readable spend that proved none, null when unknown.</returns>
    public static async Task<bool?> ClaimedAsync(
        IClientTransport transport, IVtxoStorage vtxos, ArkadeSwapIntent intent, CancellationToken cancellationToken)
    {
        if (intent.PaymentHash is not { Length: > 0 } hash) return null;
        var fate = await LockupFateReader.ReadAsync(transport, vtxos, intent.SwapPkScript, hash, cancellationToken);
        return fate.Fate switch
        {
            LockupFate.Claimed => true,
            LockupFate.Returned => false,
            _ => null,
        };
    }

    // A proven non-claim spend returned the money to whoever funded the lockup: on a send, that is us.
    public static ArkadeSwapIntentStatus AfterNoClaim(ArkadeSwapIntentType type, ArkadeSwapIntentStatus next) =>
        next == ArkadeSwapIntentStatus.Resolved && IsSend(type) ? ArkadeSwapIntentStatus.Cancelled : next;
}
