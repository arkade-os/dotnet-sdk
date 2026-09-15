using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.Core;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.ArkadeIntents.Evm;

/// <summary>A locally derived Arkade lockup and its unattended-refund capability.</summary>
/// <param name="Contract">The exact matching VHTLCv2.</param>
/// <param name="HasEmulatorRefundPath">Whether the ninth leaf is present.</param>
/// <param name="EightLeafAddress">Locally derived legacy candidate.</param>
/// <param name="NineLeafAddress">Locally derived unattended-refund candidate.</param>
public sealed record EvmArkadeLockupValidation(
    VHTLCv2Contract Contract,
    bool HasEmulatorRefundPath,
    string EightLeafAddress,
    string NineLeafAddress);

/// <summary>Rebuilds and validates the Arkade half of an EVM send before it is funded.</summary>
public static class EvmArkadeLockupValidator
{
    /// <summary>Matches the quote against the eight- and nine-leaf local derivations.</summary>
    /// <param name="request">Original request.</param>
    /// <param name="quote">Binding solver quote.</param>
    /// <param name="policy">Local acceptance policy.</param>
    /// <param name="serverInfo">Live Arkade operator facts.</param>
    /// <param name="clientRefund">Local refund descriptor.</param>
    /// <param name="refundPkScript">Local Arkade refund script.</param>
    /// <param name="emulatorPubkeyOverride">Explicit emulator key, when not network-derived.</param>
    /// <returns>The matching locally derived contract and capability.</returns>
    public static EvmArkadeLockupValidation Validate(
        EvmSendRfqRequest request,
        RfqQuote<EvmSendQuoteProfile> quote,
        EvmSendPolicy policy,
        ArkServerInfo serverInfo,
        OutputDescriptor clientRefund,
        byte[] refundPkScript,
        string? emulatorPubkeyOverride = null)
    {
        var clientXOnly = Convert.ToHexString(clientRefund.ToXOnlyPubKey().ToBytes()).ToLowerInvariant();
        if (clientXOnly != request.Profile.ClientRefundPubkey)
            throw new EvmSendQuoteException("client refund descriptor does not match the requested x-only key");
        var expectedRefund = ArkAddress.FromScriptPubKey(
            new Script(refundPkScript), serverInfo.SignerKey.ToXOnlyPubKey()).ToString(serverInfo.Network == Network.Main);
        if (expectedRefund != request.Profile.RefundAddress)
            throw new EvmSendQuoteException("refund script does not match the requested Arkade address");
        var profile = quote.Profile ?? throw new EvmSendQuoteException("quote has no EVM profile");
        if (profile.ReceiverPkScript is null)
            throw new EvmSendQuoteException("quote has no receiver_pk_script");

        var emulator = LightningCorridor.NormalizeToXOnly(Convert.FromHexString(
            NArk.Arkade.Emulator.EmulatorPubKeys.Resolve(serverInfo.NetworkName, emulatorPubkeyOverride)));
        var delays = LightningCorridor.UnilateralDelays(serverInfo);
        var candidates = LightningCorridor.DeriveBothLockupShapes(
            serverInfo.SignerKey,
            clientRefund,
            LightningCorridor.DescriptorForXOnly(quote.SolverPubkey, serverInfo.Network),
            new uint160(SwapScriptValues.PreimageHashFromPaymentHash(
                Convert.FromHexString(request.Profile.PaymentHash)), false),
            new LockTime(checked((uint)quote.RefundLocktime)),
            new Sequence(TimeSpan.FromSeconds(delays.Claim)),
            new Sequence(TimeSpan.FromSeconds(delays.Refund)),
            new Sequence(TimeSpan.FromSeconds(delays.RefundWithoutReceiver)),
            new VHTLCv2NonInteractiveClaim(Convert.FromHexString(profile.ReceiverPkScript), emulator),
            refundPkScript,
            emulator);
        var (matched, eightAddress, nineAddress) = LightningCorridor.MatchQuotedLockup(
            candidates.EightLeaf, candidates.NineLeaf, profile.LockupAddress, serverInfo.Network == Network.Main);
        if (matched is null)
            throw new LockupAddressMismatchException(eightAddress, nineAddress, profile.LockupAddress);

        var hasEmulatorRefundPath = matched.NonInteractiveRefund is { WithoutReceiver: true };
        if (policy.RequireEmulatorRefundPath && !hasEmulatorRefundPath)
            throw new EvmSendQuoteException(
                "quoted Arkade lockup has no nonInteractiveRefundWithoutReceiver leaf");
        return new EvmArkadeLockupValidation(matched, hasEmulatorRefundPath, eightAddress, nineAddress);
    }
}
