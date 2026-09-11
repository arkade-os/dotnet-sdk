using System.Security.Cryptography;
using NArk.Abstractions.Contracts;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NBitcoin;

namespace NArk.ArkadeIntents.Composition;

internal static class ComposedRouteExecutionGuard
{
    internal static bool IsCompositionOwned(ArkadeSwapIntent intent) =>
        intent.Type == ArkadeSwapIntentType.BtcToEvm || IsLinked(intent);

    internal static bool IsLinked(ArkadeSwapIntent intent) =>
        intent.Metadata.ContainsKey(ArkadeSwapMetadataKeys.ComposedOutgoingSwapId)
        || intent.Metadata.ContainsKey(ArkadeSwapMetadataKeys.ComposedPayoutPkScript);

    internal static async Task<ArkadeSwapIntent> PreparedOutgoingAsync(IArkadeIntentStorage storage,
        string outgoingSwapId, string walletId, long amountSats, string paymentHash, string payoutPkScript,
        CancellationToken cancellationToken)
    {
        RequireId(outgoingSwapId);
        var outgoing = await storage.GetArkadeSwapIntent(outgoingSwapId, cancellationToken)
            ?? throw new InvalidOperationException("composed outgoing intent must already be persisted");
        if (outgoing.Type != ArkadeSwapIntentType.BtcToEvm || outgoing.WalletId != walletId
            || outgoing.OfferAmount.Satoshi != amountSats || amountSats <= 0
            || outgoing.PaymentHash != paymentHash || outgoing.SwapPkScript != payoutPkScript)
            throw new InvalidOperationException("composed route identity, hash, amount or payout differs");
        ValidateSecret(outgoing);
        return outgoing;
    }

    internal static async Task<bool> ValidateIngressAsync(IArkadeIntentStorage storage,
        IContractStorage contracts, ArkadeSwapIntent ingress, VHTLCv2Contract contract,
        Network network, long now, CancellationToken cancellationToken)
    {
        if (!IsLinked(ingress)) return false;
        var outgoingId = ingress.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.ComposedOutgoingSwapId);
        var payout = ingress.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.ComposedPayoutPkScript);
        if (outgoingId is null || payout is null || outgoingId == ingress.Id
            || ingress.Type is not (ArkadeSwapIntentType.LightningToBtc or ArkadeSwapIntentType.OnchainToBtc)
            || contract.NonInteractiveClaim is not { } claim || new Script(claim.ReceiverPkScript).ToHex() != payout)
            throw new InvalidOperationException("composed ingress linkage is invalid");
        var outgoing = await PreparedOutgoingAsync(storage, outgoingId, ingress.WalletId,
            ingress.WantAmount.Satoshi, ingress.PaymentHash!, payout, cancellationToken);
        if (ingress.OfferAmount < ingress.WantAmount)
            throw new InvalidOperationException("composed ingress spread is negative");
        var outgoingContract = await LightningCorridor.LoadLockupAsync(contracts, outgoing.SwapPkScript,
            outgoing.Id, network, cancellationToken);
        var preimage = ValidateSecret(ingress);
        var hash = new uint160(NBitcoin.Crypto.Hashes.RIPEMD160(SHA256.HashData(preimage), 32), false);
        if (contract.Hash != hash || outgoingContract.Hash != hash)
            throw new InvalidOperationException("composed covenant hashes differ from the stored secret");
        if (ingress.Status != ArkadeSwapIntentStatus.Fulfilled
            && (ingress.Status != ArkadeSwapIntentStatus.Claimable || outgoing.Status != ArkadeSwapIntentStatus.Pending
                || outgoing.RefundLocktime is not { } refundAt || refundAt <= now
                || outgoingContract.RefundLocktime.Value != refundAt
                || outgoingContract.NonInteractiveRefund is not { WithoutReceiver: true }))
            throw new InvalidOperationException("composed ingress is not claimable into a pending outgoing lock");
        return true;
    }

    internal static ArkadeSwapIntent Bind(ArkadeSwapIntent ingress, string? outgoingId, string? payoutPkScript)
    {
        if (outgoingId is not null)
        {
            ingress.Metadata[ArkadeSwapMetadataKeys.ComposedOutgoingSwapId] = outgoingId;
            ingress.Metadata[ArkadeSwapMetadataKeys.ComposedPayoutPkScript] = payoutPkScript!;
        }
        return ingress;
    }

    internal static byte[] ValidateSecret(ArkadeSwapIntent intent)
    {
        var value = intent.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.Preimage);
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit)
            || intent.PaymentHash is null || intent.PaymentHash.Length != 64)
            throw new InvalidOperationException("composed intent has no valid stored secret and hash");
        var preimage = Convert.FromHexString(value);
        if (Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant() != intent.PaymentHash)
            throw new InvalidOperationException("composed intent secret does not match its hash");
        return preimage;
    }

    internal static void RequireId(string value)
    {
        if (value is null || value.Length != 64 || value != value.ToLowerInvariant() || !value.All(Uri.IsHexDigit))
            throw new InvalidOperationException("composed RFQ identity must be lowercase 32-byte hex");
    }
}
