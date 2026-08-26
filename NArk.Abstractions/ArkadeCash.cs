using System.Buffers.Binary;
using System.Security.Cryptography;
using NArk.Abstractions.Contracts;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions;

/// <summary>
/// An ArkadeCash bearer instrument: a private key plus the contract parameters needed to
/// rebuild the payment contract it funds, encoded as a single bech32m string.
/// Whoever holds the string controls the funds, so it can be handed over without exchanging
/// an Arkade address first.
/// </summary>
/// <remarks>
/// Encoding: <c>arkadecash1...</c> (mainnet) or <c>tarkadecash1...</c> (testnet/regtest),
/// over a 69-byte payload of version (1 byte) + private key (32 bytes) +
/// Arkade server public key (32 bytes) + BIP68 CSV sequence (4 bytes, big-endian).
/// This matches the ArkadeCash format used by the TypeScript SDK.
/// </remarks>
public class ArkadeCash: IDisposable
{
    private const byte Version = 0x00;
    private const int PayloadLength = 1 + 32 + 32 + 4;
    
    private const string HrpMainnet = "arkadecash";
    private const string HrpTestnet = "tarkadecash";
    
    private static readonly Bech32Encoder MainnetEncoder;
    private static readonly Bech32Encoder TestnetEncoder;

    /// <summary>The human-readable prefix this instrument encodes with: <c>arkadecash</c> or <c>tarkadecash</c>.</summary>
    public string Hrp { get; }

    /// <summary>The private key carried by the instrument; spending its VTXOs requires nothing else.</summary>
    public ECPrivKey PrivKey { get; }

    /// <summary>The x-only public key derived from <see cref="PrivKey"/>, used as the owner key of the contract.</summary>
    public ECXOnlyPubKey Pubkey { get; }

    /// <summary>The x-only public key of the Arkade server co-signing the contract.</summary>
    public ECXOnlyPubKey ServerPubkey { get; }

    /// <summary>The relative timelock (BIP68 sequence) of the contract's unilateral exit path.</summary>
    public Sequence LockTime { get; }
    

    static ArkadeCash()
    {
        MainnetEncoder = Encoders.Bech32(HrpMainnet);
        MainnetEncoder.StrictLength = false;
        MainnetEncoder.SquashBytes = true;

        TestnetEncoder = Encoders.Bech32(HrpTestnet);
        TestnetEncoder.StrictLength = false;
        TestnetEncoder.SquashBytes = true;
    }
    
    /// <summary>Creates an ArkadeCash instrument around an existing private key.</summary>
    /// <param name="privKey">The private key that controls the funds. The instance takes ownership and disposes it.</param>
    /// <param name="serverPubkey">The x-only public key of the Arkade server.</param>
    /// <param name="lockTime">The relative timelock (BIP68 sequence) of the unilateral exit path.</param>
    /// <param name="hrp">The bech32m prefix: <c>arkadecash</c> (default) or <c>tarkadecash</c>.</param>
    /// <exception cref="ArgumentException">The prefix is neither <c>arkadecash</c> nor <c>tarkadecash</c>.</exception>
    public ArkadeCash(ECPrivKey privKey, ECXOnlyPubKey serverPubkey, Sequence lockTime, string hrp = "arkadecash")
    {
        
        if (hrp is not ("tarkadecash" or "arkadecash"))
        {
            throw new ArgumentException($"Invalid hrp: {hrp}. Supported arguments: {HrpMainnet},  {HrpTestnet}");
        }
        
        this.PrivKey = privKey;
        this.Pubkey = privKey.CreateXOnlyPubKey();
        this.ServerPubkey = serverPubkey;
        this.LockTime = lockTime;
        this.Hrp = hrp;
    }

    /// <summary>Generates an ArkadeCash instrument with a freshly generated random private key.</summary>
    /// <param name="serverPubkey">The x-only public key of the Arkade server.</param>
    /// <param name="locktime">The relative timelock (BIP68 sequence) of the unilateral exit path.</param>
    /// <param name="hrp">The bech32m prefix; defaults to <c>arkadecash</c> when omitted.</param>
    /// <returns>A new instrument holding the generated key.</returns>
    public static ArkadeCash Generate(ECXOnlyPubKey serverPubkey, Sequence locktime, string? hrp = null)
    {
        var pk = RandomUtils.GetBytes(32);
        try
        {
            if (!ECPrivKey.TryCreate(pk, out var key))
            {
                throw new InvalidOperationException("Could not generate ArkadeCash address!");
            }

            if (hrp is null)
            {
                return new ArkadeCash(key, serverPubkey, locktime);
            }

            return new ArkadeCash(key, serverPubkey, locktime, hrp);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pk);
        }
    }

    /// <summary>Encodes the instrument as its bech32m string, e.g. <c>arkadecash1...</c>.</summary>
    /// <returns>The encoded ArkadeCash string. It carries the private key — treat it as a secret.</returns>
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

    /// <summary>Decodes an ArkadeCash string produced by <see cref="ToString"/>.</summary>
    /// <param name="encoded">The bech32m string; surrounding whitespace and casing are normalised.</param>
    /// <returns>The decoded instrument.</returns>
    /// <exception cref="FormatException">The prefix, checksum, payload length, or version is invalid.</exception>
    public static ArkadeCash Parse(string encoded)
    {
        encoded = encoded.Trim().ToLowerInvariant();
        var encoder = 
            encoded.StartsWith(HrpMainnet) ? MainnetEncoder : 
            encoded.StartsWith(HrpTestnet) ? TestnetEncoder : 
            throw new FormatException($"Invalid ArkadeCash HRP: {encoded}");
        
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
        

        return new ArkadeCash(privKey, serverPubkey, locktime, encoded.StartsWith(HrpMainnet) ? HrpMainnet : HrpTestnet);
    }

    /// <summary>Attempts to decode an ArkadeCash string, without throwing on malformed input.</summary>
    /// <param name="encoded">The bech32m string to decode.</param>
    /// <param name="arkadeCash">The decoded instrument, or <c>null</c> when decoding fails.</param>
    /// <returns><c>true</c> when the string decoded successfully; otherwise <c>false</c>.</returns>
    public static bool TryParse(string encoded, out ArkadeCash? arkadeCash)
    {
        arkadeCash = null;
        try
        {
            arkadeCash = Parse(encoded);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Disposes the private key held by this instrument.</summary>
    public void Dispose()
    {
        PrivKey.Dispose();
    }
}
