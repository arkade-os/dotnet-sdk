using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Extensions;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Tests;

/// <summary>
/// Signer-cutoff awareness of <see cref="ArkCoin.CanSpendOffchain(TimeHeight, System.Collections.Generic.IReadOnlyDictionary{ECXOnlyPubKey, long})"/>:
/// a coin under a deprecated Arkade signer whose cutoff has passed can no longer be collaboratively
/// spent offchain (the operator won't co-sign), so it must be kept out of offchain-spend selection —
/// while remaining NOT recoverable (rotation regime 2: wait until expiry).
/// </summary>
[TestFixture]
public class ArkCoinTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeHeight CurrentTime = new(Now, 100);
    private static long NowUnix => Now.ToUnixTimeSeconds();

    private static readonly ECXOnlyPubKey CurrentSigner = NewKey();
    private static readonly ECXOnlyPubKey DeprecatedSigner = NewKey();

    [Test]
    public void Deprecated_signer_past_cutoff_is_not_spendable_offchain()
    {
        var coin = MakeCoin(DeprecatedSigner);
        var deprecated = Deprecated(DeprecatedSigner, NowUnix - 3600); // cutoff in the past

        Assert.That(coin.IsDeprecatedSignerPastCutoff(deprecated, NowUnix), Is.True);
        Assert.That(coin.CanSpendOffchain(CurrentTime, deprecated), Is.False,
            "operator no longer co-signs past the cutoff");
        // Regime 2: it must NOT be recoverable yet (waits until expiry), and the signer-blind
        // overload still reports it spendable — proving the signer check is what excludes it.
        Assert.That(coin.IsRecoverable(CurrentTime), Is.False);
        Assert.That(coin.CanSpendOffchain(CurrentTime), Is.True);
    }

    [Test]
    public void Deprecated_signer_future_cutoff_is_still_spendable_offchain()
    {
        var coin = MakeCoin(DeprecatedSigner);
        var deprecated = Deprecated(DeprecatedSigner, NowUnix + 3600); // cutoff in the future

        Assert.That(coin.IsDeprecatedSignerPastCutoff(deprecated, NowUnix), Is.False);
        Assert.That(coin.CanSpendOffchain(CurrentTime, deprecated), Is.True,
            "within cutoff the operator still co-signs (regime 1, sweepable)");
    }

    [Test]
    public void Deprecated_signer_zero_cutoff_is_still_spendable_offchain()
    {
        var coin = MakeCoin(DeprecatedSigner);
        var deprecated = Deprecated(DeprecatedSigner, 0); // 0 == no cutoff

        Assert.That(coin.IsDeprecatedSignerPastCutoff(deprecated, NowUnix), Is.False);
        Assert.That(coin.CanSpendOffchain(CurrentTime, deprecated), Is.True);
    }

    [Test]
    public void Current_signer_is_spendable_offchain_even_with_a_deprecated_set()
    {
        var coin = MakeCoin(CurrentSigner);
        var deprecated = Deprecated(DeprecatedSigner, NowUnix - 3600);

        Assert.That(coin.IsDeprecatedSignerPastCutoff(deprecated, NowUnix), Is.False);
        Assert.That(coin.CanSpendOffchain(CurrentTime, deprecated), Is.True);
    }

    [Test]
    public void Empty_deprecated_set_is_spendable_offchain()
    {
        var coin = MakeCoin(DeprecatedSigner);
        var empty = new Dictionary<ECXOnlyPubKey, long>();

        Assert.That(coin.IsDeprecatedSignerPastCutoff(empty, NowUnix), Is.False);
        Assert.That(coin.CanSpendOffchain(CurrentTime, empty), Is.True);
    }

    [Test]
    public void Recoverable_coin_is_not_spendable_offchain_regardless_of_signer()
    {
        var swept = MakeCoin(CurrentSigner, swept: true);
        var empty = new Dictionary<ECXOnlyPubKey, long>();

        Assert.That(swept.CanSpendOffchain(CurrentTime, empty), Is.False,
            "swept / recoverable coins are never offchain-spendable");
    }

    private static ECXOnlyPubKey NewKey()
        => ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());

    private static Dictionary<ECXOnlyPubKey, long> Deprecated(ECXOnlyPubKey key, long cutoff)
        => new() { { key, cutoff } };

    private static ArkCoin MakeCoin(ECXOnlyPubKey serverKey, bool swept = false)
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var contract = new GenericArkContract(serverKey.ToOutputDescriptor(Network.RegTest), [script]);
        var userKey = NewKey().ToOutputDescriptor(Network.RegTest);
        return new ArkCoin(
            walletIdentifier: "test-wallet",
            contract: contract,
            birth: Now,
            expiresAt: Now.AddDays(30),
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.One, 0),
            txOut: new TxOut(Money.Satoshis(100_000), Script.Empty),
            signerDescriptor: userKey,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: swept,
            unrolled: false);
    }
}
