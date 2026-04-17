using NArk.Abstractions;
using NBitcoin;
using NBitcoin.Secp256k1;
using NArk.Core.Extensions;
namespace NArk.Tests;

public class ArkCashTests
{
    private static readonly ECPrivKey TestPrivKey = 
            ECPrivKey.Create(
                Convert.FromHexString("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20")
                );
    private static readonly ECXOnlyPubKey TestPubKey = TestPrivKey.CreateXOnlyPubKey();
    
    private static readonly ECPrivKey ServerPrivkey =
        ECPrivKey.Create(
            Convert.FromHexString("a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0")
            );

    private static readonly ECXOnlyPubKey ServerPubkey = ServerPrivkey.CreateXOnlyPubKey();
    
    [Test]
    [TestCase("arkcash")]
    [TestCase("tarkcash")]
    [TestCase(null)]
    public void RoundtripsEncodeMainnet(string? hrp)
    {
        var locktime = new Sequence(144);
        var cash = hrp is not null ? new ArkCash(TestPrivKey, ServerPubkey, locktime, hrp) : new ArkCash(TestPrivKey, ServerPubkey, locktime);
        var encoded = cash.ToString();
        
        Assert.That(encoded.StartsWith(hrp ?? "arkcash"), Is.True);
        Assert.That(ArkCash.TryParse(encoded, out var parsed));
        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed.LockTime, Is.EqualTo(locktime));
        Assert.That(parsed.Pubkey.ToBytes(), Is.EqualTo(TestPrivKey.CreateXOnlyPubKey().ToBytes()));
        Assert.That(parsed.ServerPubkey.ToBytes(), Is.EqualTo(ServerPubkey.ToBytes()));
    }
    
    [Test]
    public void IdentityTests()
    {
        var cash = new ArkCash(TestPrivKey, ServerPubkey, new Sequence(144));
        Assert.That(cash.Pubkey.ToBytes(), Is.EqualTo(TestPubKey.ToBytes()));
        Assert.That(cash.PrivKey.Equals(TestPrivKey), Is.True);
    }

    [Test]
    public void CreatesValidVtxoScript()
    {
        var cash = new ArkCash(TestPrivKey, ServerPubkey, new Sequence(144));

        var contract = cash.ToContract(Network.Main);
        Script pkScript = contract.GetScriptPubKey();
        var address = cash.GetAddress(Network.Main);

        Assert.That(pkScript, Is.EqualTo(address.ScriptPubKey));
        Assert.That(pkScript, Is.Not.Null);
        Assert.That(pkScript, Has.Length);
    }

    [Test]
    public void CreatesValidArkAddress()
    {
        var cash = new ArkCash(TestPrivKey, ServerPubkey, new Sequence(144));
        var address = cash.GetAddress(Network.Main);
        
        // encode as mainnet
        var encoded = address.ToString(true);
        Assert.That(encoded.StartsWith("ark1"), Is.True);
        // encode as testntet 
        var encoded2 = address.ToString(false);
        Assert.That(encoded2.StartsWith("tark1"), Is.True);
    }

    [Test]
    public void GeneratesRandomArkCash()
    {
        var cash1 = ArkCash.Generate(ServerPubkey, new Sequence(144));
        var cash2 = ArkCash.Generate(ServerPubkey, new Sequence(144));
        
        Assert.That(cash1, Is.Not.Null);
        Assert.That(cash1.Pubkey, Is.Not.Null);
        Assert.That(cash1.ServerPubkey, Is.Not.Null);
        Assert.That(cash1.PrivKey, Is.Not.Null);
        
        Assert.That(cash2.Pubkey, Is.Not.Null);
        Assert.That(cash2.ServerPubkey, Is.Not.Null);
        Assert.That(cash2.PrivKey, Is.Not.Null);
        
        Assert.That(cash1.Pubkey.ToBytes(), Is.Not.EqualTo(cash2.Pubkey.ToBytes()));
        Assert.That(cash1.PrivKey, Is.Not.EqualTo(cash2.PrivKey));
        
        Assert.That(cash1.ServerPubkey, Is.EqualTo(cash2.ServerPubkey));
        Assert.That(cash2.Hrp, Is.EqualTo(cash1.Hrp));
    }

    [Test]
    public void ShouldRejectInvalidData()
    {
        // invalid privkey 
        Assert.Throws<FormatException>(() => ArkCash.Parse("invalidarkcash"));
        Assert.That(ArkCash.TryParse("invalidarkcash", out var probablyNull), Is.False);
        Assert.That(probablyNull, Is.Null);
        
        // wrong data length
        Assert.Throws<FormatException>(() => ArkCash.Parse("arkcash1qqqqqqqqq0saqvp"));
        Assert.That(ArkCash.TryParse("arkcash1qqqqqqqqq0saqvp", out var probablyNullToo), Is.False);
        Assert.That(probablyNullToo, Is.Null);
    }


    [OneTimeTearDown]
    public void Cleanup()
    {
        TestPrivKey.Dispose();
        ServerPrivkey.Dispose();
    }
}
