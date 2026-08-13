using NArk.Abstractions.Extensions;
using NArk.Core;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// An <see cref="ArkServerInfo"/> carrying nothing but a unilateral exit delay.
/// </summary>
/// <remarks>
/// Shared by the tests that exercise the delay derivation. Every other field is filler, which is
/// itself part of what those tests pin: the derivation reads
/// <see cref="ArkServerInfo.UnilateralExit"/> and nothing else.
/// </remarks>
internal static class TestServerInfo
{
    /// <summary>Server info advertising <paramref name="unilateralExit"/>.</summary>
    internal static ArkServerInfo With(Sequence unilateralExit) => new(
        Dust: Money.Satoshis(546),
        SignerKey: KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest),
        DeprecatedSigners: new Dictionary<ECXOnlyPubKey, long>(ECXOnlyPubKeyComparer.Instance),
        Network: Network.RegTest,
        UnilateralExit: unilateralExit,
        BoardingExit: new Sequence(144),
        ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
        ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
        CheckpointTapScript: new UnilateralPathArkTapScript(
            new Sequence(144), new NofNMultisigTapScript([])),
        FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
        Digest: "");

    /// <summary>
    /// Server info advertising <paramref name="seconds"/>, encoded the way the transports encode it.
    /// </summary>
    /// <remarks>
    /// Rounded up, because <see cref="Sequence"/> truncates to 512-second units and an operator
    /// advertising 3600s must not come back as a 3584s requirement — shorter than it asked for.
    /// Building the sequence directly would reintroduce that truncation and test a value no
    /// transport would ever produce.
    /// </remarks>
    internal static ArkServerInfo WithSeconds(uint seconds)
    {
        const uint granularity = 512;
        var units = (seconds + granularity - 1) / granularity;
        return With(new Sequence(TimeSpan.FromSeconds(units * granularity)));
    }
}
