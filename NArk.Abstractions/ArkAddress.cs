using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions;

/// <summary>
/// Bech32m-encoded Arkade address: a taproot x-only pubkey tagged with a server key and protocol version.
/// Serialized as <c>ark1…</c> (mainnet) or <c>tark1…</c> (testnet).
/// </summary>
public class ArkAddress : TaprootPubKey
{
    private static Bech32Encoder TestnetEncoder { get; set; }
    private static readonly Bech32Encoder MainnetEncoder;
    private const string HrpMainnet = "ark";
    private const string HrpTestnet = "tark";

    static ArkAddress()
    {
        MainnetEncoder = Encoders.Bech32(HrpMainnet);
        MainnetEncoder.StrictLength = false;
        MainnetEncoder.SquashBytes = true;

        TestnetEncoder = Encoders.Bech32(HrpTestnet);
        TestnetEncoder.StrictLength = false;
        TestnetEncoder.SquashBytes = true;
    }

    /// <inheritdoc/>
    public ArkAddress(TaprootAddress taprootAddress, ECXOnlyPubKey serverKey, int version = 0, Network? network = null) : base(taprootAddress.PubKey.ToBytes())
    {
        ArgumentNullException.ThrowIfNull(taprootAddress);
        ArgumentNullException.ThrowIfNull(serverKey);

        ServerKey = serverKey;
        Version = version;
        IsMainnet = network is not null ? network == Network.Main : null;
    }

#pragma warning disable CS1591
    public ArkAddress(ECXOnlyPubKey tweakedKey, ECXOnlyPubKey serverKey, int version = 0) : this(tweakedKey, serverKey, version, null)
    {

    }
    public ArkAddress(ECXOnlyPubKey tweakedKey, ECXOnlyPubKey serverKey, int version, bool? isMainnet) : base(tweakedKey.ToBytes())
#pragma warning restore CS1591
    {
        ArgumentNullException.ThrowIfNull(tweakedKey);
        ArgumentNullException.ThrowIfNull(serverKey);

        ServerKey = serverKey;
        Version = version;
        IsMainnet = isMainnet;
    }

    /// <summary>The Arkade server's x-only pubkey embedded in this address.</summary>
    public ECXOnlyPubKey ServerKey { get; }
    /// <summary>Address format version byte. Currently always 0.</summary>
    public int Version { get; }
    private bool? IsMainnet { get; }

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public override string ToString()
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    {
        return IsMainnet is null ?
            throw new InvalidOperationException("Network is required for address generation") :
            ToString(IsMainnet.Value);
    }

    /// <summary>Encodes the address using the mainnet or testnet bech32m HRP.</summary>
    public string ToString(bool isMainnet)
    {
        var encoder = isMainnet ? MainnetEncoder : TestnetEncoder;
        byte[] bytes = [Convert.ToByte(Version), .. ServerKey.ToBytes(), .. ToBytes()];
        return encoder.EncodeData(bytes, Bech32EncodingType.BECH32M);
    }

    /// <summary>Extracts the x-only pubkey from a taproot scriptPubKey and wraps it as an <see cref="ArkAddress"/>.</summary>
    public static ArkAddress FromScriptPubKey(Script scriptPubKey, ECXOnlyPubKey serverKey)
    {
        var key = PayToTaprootTemplate.Instance.ExtractScriptPubKeyParameters(scriptPubKey);
        if (key is null)
            throw new FormatException("Could not extract Taproot parameters from scriptPubKey");
        var pubKey = ECXOnlyPubKey.Create(key.ToBytes());
        return new ArkAddress(pubKey, serverKey);
    }

    /// <summary>Decodes a bech32m Arkade address string. Throws <see cref="FormatException"/> on invalid input.</summary>
    public new static ArkAddress Parse(string address)
    {
        address = address.ToLowerInvariant();

        var encoder = address.StartsWith(HrpMainnet) ? MainnetEncoder :
            address.StartsWith(HrpTestnet) ? TestnetEncoder : throw new FormatException($"Invalid Ark address: {address}");
        var data = encoder.DecodeDataRaw(address, out var type);

        if (type != Bech32EncodingType.BECH32M || data.Length != 65)
            throw new FormatException($"Invalid Ark address: {address}");

        var version = data[0];
        var serverKey = ECXOnlyPubKey.Create(data.Skip(1).Take(32).ToArray());
        var tweakedKey = ECXOnlyPubKey.Create(data.Skip(33).ToArray());

        return new ArkAddress(tweakedKey, serverKey, version, encoder == MainnetEncoder);
    }

    /// <summary>Tries to decode a bech32m Arkade address string. Returns false without throwing on invalid input.</summary>
    public static bool TryParse(string address, out ArkAddress? arkAddress)
    {
        try
        {
            arkAddress = Parse(address);
            return true;
        }
        catch (Exception)
        {
            arkAddress = null;
            return false;
        }
    }
}