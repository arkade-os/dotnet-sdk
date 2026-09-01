using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Arkade.Contracts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Tests.ArkadeIntents.Lightning;

/// <summary>
/// Building the spend that takes delivery of a receive swap.
/// </summary>
/// <remarks>
/// Claiming publishes the preimage, which is also how the counterparty gets paid — so a spend built
/// around the wrong secret is not a private mistake that bounces harmlessly. It is a broadcast that
/// fails validation after the attempt is already out, on a swap with a deadline. The checks here
/// happen before a coin exists at all.
/// </remarks>
[TestFixture]
public class VHTLCv2ClaimCoinTests
{
    private static readonly byte[] Preimage = Enumerable.Repeat((byte)0x11, 32).ToArray();

    [Test]
    public void ClaimCoin_CarriesThePreimageAsTheConditionWitness()
    {
        var coin = Contract().ToClaimCoin("wallet", Vtxo(), Preimage);

        // The on-chain condition witness, not the arkade-script one: the claim leaf gates on a
        // hashlock and carries no covenant.
        Assert.That(coin.SpendingConditionWitness, Is.Not.Null);
        Assert.That(
            Convert.ToHexString(coin.SpendingConditionWitness!.Pushes.Single()).ToLowerInvariant(),
            Is.EqualTo(Convert.ToHexString(Preimage).ToLowerInvariant()));
    }

    [Test]
    public void ClaimCoin_SignsAsTheReceiver()
    {
        // Roles are positional, and on this corridor the client is the receiver. Signing as the
        // sender would build a witness for a leaf we hold no key for.
        var contract = Contract();

        var coin = contract.ToClaimCoin("wallet", Vtxo(), Preimage);

        Assert.That(coin.SignerDescriptor?.ToString(), Is.EqualTo(contract.Receiver.ToString()));
    }

    [Test]
    public void ClaimCoin_RefusesAPreimageForADifferentSecret()
    {
        var wrong = Enumerable.Repeat((byte)0x22, 32).ToArray();

        var ex = Assert.Throws<ArgumentException>(() => Contract().ToClaimCoin("wallet", Vtxo(), wrong));

        Assert.That(ex!.Message, Does.Contain("committed digest"));
    }

    [Test]
    public void ClaimCoin_RefusesAPreimageOfTheWrongLength()
    {
        // The leaf gates on 32 bytes before it hashes anything, so a shorter secret cannot satisfy
        // it however it hashes.
        var ex = Assert.Throws<ArgumentException>(() =>
            Contract().ToClaimCoin("wallet", Vtxo(), Preimage[..31]));

        Assert.That(ex!.Message, Does.Contain("32 bytes"));
    }

    [Test]
    public void ClaimCoin_RefusesAnAlreadySpentLockup()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Contract().ToClaimCoin("wallet", Vtxo(spentBy: "tx"), Preimage));
    }

    private static VHTLCv2Contract Contract() => new(
        Descriptor(0x02), Descriptor(0x03), Descriptor(0x04),
        new uint160(NBitcoin.Crypto.Hashes.RIPEMD160(NBitcoin.Crypto.Hashes.SHA256(Preimage), 32), false),
        new LockTime(1_800_000_000),
        new Sequence(TimeSpan.FromSeconds(4096)),
        new Sequence(TimeSpan.FromSeconds(4608)),
        new Sequence(TimeSpan.FromSeconds(5120)),
        // Model the shape a deployed solver-quoted lockup actually carries —
        // pre-timelocked-refund, same as every production call site.
        new NonInteractiveParameters(
            ECXOnlyPubKey.Create(KeyBytes(0x05)),
            receiverPkScript: [0x51, 0x20, .. KeyBytes(0x06)],
            senderPkScript: [0x51, 0x20, .. KeyBytes(0x07)],
            NonInteractiveParametersLegacy.PreTimelockedRefund));

    private static OutputDescriptor Descriptor(byte fill) =>
        KeyExtensions.ParseOutputDescriptor(
            "02" + Convert.ToHexString(KeyBytes(fill)).ToLowerInvariant(), Network.RegTest);

    /// <summary>A valid x-only key: fill a scalar and take the public key it derives.</summary>
    private static byte[] KeyBytes(byte fill) =>
        new Key(Enumerable.Repeat(fill, 32).ToArray()).PubKey.Compress().ToBytes()[1..];

    private static ArkVtxo Vtxo(string? spentBy = null) =>
        new(Script: "5120" + new string('a', 64), TransactionId: new string('b', 64), TransactionOutputIndex: 0, Amount: 50_000,
            SpentByTransactionId: spentBy, SettledByTransactionId: null, Swept: false,
            CreatedAt: DateTimeOffset.UtcNow, ExpiresAt: null, ExpiresAtHeight: null, ArkTxid: null);
}
