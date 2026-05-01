using System.Buffers.Binary;
using System.Security.Cryptography;
using NArk.Abstractions.Contracts;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions;

public class ArkCash: IDisposable
{
    private const byte Version = 0x00;
    private const int PayloadLength = 1 + 32 + 32 + 4;
    
    private const string HrpMainnet = "arkcash";
    private const string HrpTestnet = "tarkcash";
    
    private static readonly Bech32Encoder MainnetEncoder;
    private static readonly Bech32Encoder TestnetEncoder;

    public string Hrp { get; }
    public ECPrivKey PrivKey { get; }
    public ECXOnlyPubKey Pubkey { get; }
    public ECXOnlyPubKey ServerPubkey { get; }
    public Sequence LockTime { get; }
    

    static ArkCash()
    {
        MainnetEncoder = Encoders.Bech32(HrpMainnet);
        MainnetEncoder.StrictLength = false;
        MainnetEncoder.SquashBytes = true;

        TestnetEncoder = Encoders.Bech32(HrpTestnet);
        TestnetEncoder.StrictLength = false;
        TestnetEncoder.SquashBytes = true;
    }
    
    public ArkCash(ECPrivKey privKey, ECXOnlyPubKey serverPubkey, Sequence lockTime, string hrp = "arkcash")
    {
        
        if (hrp is not ("tarkcash" or "arkcash"))
        {
            throw new ArgumentException($"Invalid hrp: {hrp}. Supported arguments: {HrpMainnet},  {HrpTestnet}");
        }
        
        this.PrivKey = privKey;
        this.Pubkey = privKey.CreateXOnlyPubKey();
        this.ServerPubkey = serverPubkey;
        this.LockTime = lockTime;
        this.Hrp = hrp;
    }

    public static ArkCash Generate(ECXOnlyPubKey serverPubkey, Sequence locktime, string? hrp = null)
    {
        var pk = RandomUtils.GetBytes(32);
        try
        {
            if (!ECPrivKey.TryCreate(pk, out var key))
            {
                throw new InvalidOperationException("Could not generate ArkCash address!");
            }

            if (hrp is null)
            {
                return new ArkCash(key, serverPubkey, locktime);
            }

            return new ArkCash(key, serverPubkey, locktime, hrp);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pk);
        }
    }

    public override string ToString()
    {
        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = Version;
        PrivKey.WriteToSpan(payload[1..33]);
        ServerPubkey.WriteToSpan(payload[33..65]);
        BinaryPrimitives.WriteUInt32BigEndian(payload[65..69], LockTime.Value);
        
        var encoder = Hrp switch
        {
            HrpMainnet => MainnetEncoder,
            HrpTestnet => TestnetEncoder,
            _ => throw new InvalidOperationException()
        };

        return encoder.EncodeData(payload, Bech32EncodingType.BECH32M);
    }

    public static ArkCash Parse(string encoded)
    {
        encoded = encoded.Trim().ToLowerInvariant();
        var encoder = 
            encoded.StartsWith(HrpMainnet) ? MainnetEncoder : 
            encoded.StartsWith(HrpTestnet) ? TestnetEncoder : 
            throw new FormatException($"Invalid ArkCash HRP: {encoded}");
        
        var decodedRaw = encoder.DecodeDataRaw(encoded, out _);
        if (decodedRaw == null)
        {
            throw new FormatException("Could not decode encoded data");
        }
        
        var payload = decodedRaw.AsSpan();
        if (payload.Length != PayloadLength)
        {
            throw new FormatException($"Invalid payload length! (Expected: {PayloadLength} bytes, got {payload.Length})");
        }
        if (payload[0] != Version)
        {
            throw new FormatException($"Invalid version! {payload[0]}");
        }
        var privKey = ECPrivKey.Create(payload[1..33]);
        var serverPubkey = ECXOnlyPubKey.Create(payload[33..65]);
        var lockTimeVal = BinaryPrimitives.ReadUInt32BigEndian(payload[65..69]);
        var locktime = new Sequence(lockTimeVal);
        

        return new ArkCash(privKey, serverPubkey, locktime, encoded.StartsWith(HrpMainnet) ? HrpMainnet : HrpTestnet);
    }

    public static bool TryParse(string encoded, out ArkCash? arkCash)
    {
        arkCash = null;
        try
        {
            arkCash = Parse(encoded);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        PrivKey.Dispose();
    }
}
