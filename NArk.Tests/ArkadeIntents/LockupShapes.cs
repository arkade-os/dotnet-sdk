using NArk.Abstractions.Extensions;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents;

/// <summary>
/// The pair of lockup shapes every corridor's address gate has to choose between, built from one
/// shared, otherwise-arbitrary parameter set.
/// </summary>
/// <remarks>
/// Shared by all three corridors' gate tests rather than mirrored in each, for the same reason
/// <see cref="LightningCorridor.DeriveBothLockupShapes"/> itself is shared: three copies of the
/// construction is three places for the candidates to drift into shapes the gate was never meant to
/// compare.
/// </remarks>
internal static class LockupShapes
{
    /// <summary>Both candidate lockup shapes, differing in nothing but the timelocked refund leaf.</summary>
    internal static (VHTLCv2Contract EightLeaf, VHTLCv2Contract NineLeaf) Candidates() =>
        LightningCorridor.DeriveBothLockupShapes(
            RandomDescriptor(),
            RandomDescriptor(),
            RandomDescriptor(),
            new uint160(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20), false),
            new LockTime(1_800_600_000),
            new Sequence(TimeSpan.FromSeconds(512)),
            new Sequence(TimeSpan.FromSeconds(512)),
            new Sequence(TimeSpan.FromSeconds(1024)),
            new VHTLCv2NonInteractiveClaim(RandomP2trPkScript(), RandomXOnly()),
            refundPkScript: RandomP2trPkScript(),
            refundEmulatorPubKey: RandomXOnly());

    internal static OutputDescriptor RandomDescriptor() =>
        KeyExtensions.ParseOutputDescriptor(new Key().PubKey.ToHex(), Network.RegTest);

    internal static ECXOnlyPubKey RandomXOnly() =>
        ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());

    internal static byte[] RandomP2trPkScript()
    {
        var script = new byte[34];
        script[0] = 0x51;
        script[1] = 0x20;
        new Key().PubKey.TaprootInternalKey.ToBytes().CopyTo(script, 2);
        return script;
    }
}
