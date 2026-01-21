using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions;

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

    public ArkAddress(TaprootAddress taprootAddress, ECXOnlyPubKey serverKey, int version = 0, Network? network = null) : base(taprootAddress.PubKey.ToBytes())
    {
        ArgumentNullException.ThrowIfNull(taprootAddress);
        ArgumentNullException.ThrowIfNull(serverKey);

        ServerKey = serverKey;
        Version = version;
        IsMainnet = network is not null ? network == Network.Main : null;
    }

    public ArkAddress(ECXOnlyPubKey tweakedKey, ECXOnlyPubKey serverKey, int version = 0) : this(tweakedKey, serverKey, version, null)
    {

    }
    public ArkAddress(ECXOnlyPubKey tweakedKey, ECXOnlyPubKey serverKey, int version, bool? isMainnet) : base(tweakedKey.ToBytes())
    {
        ArgumentNullException.ThrowIfNull(tweakedKey);
        ArgumentNullException.ThrowIfNull(serverKey);

        ServerKey = serverKey;
        Version = version;
        IsMainnet = isMainnet;
    }

    public ECXOnlyPubKey ServerKey { get; }
    public int Version { get; }
    private bool? IsMainnet { get; }

    public override string ToString()
    {
        return IsMainnet is null ?
            throw new InvalidOperationException("Network is required for address generation") :
            ToString(IsMainnet.Value);
    }

    public string ToString(bool isMainnet)
    {
        var encoder = isMainnet ? MainnetEncoder : TestnetEncoder;
        byte[] bytes = [Convert.ToByte(Version), .. ServerKey.ToBytes(), .. ToBytes()];
        return encoder.EncodeData(bytes, Bech32EncodingType.BECH32M);
    }

    public static ArkAddress FromScriptPubKey(Script scriptPubKey, ECXOnlyPubKey serverKey)
    {
        var key = PayToTaprootTemplate.Instance.ExtractScriptPubKeyParameters(scriptPubKey);
        if (key is null)
            throw new FormatException("Could not extract Taproot parameters from scriptPubKey");
        var pubKey = ECXOnlyPubKey.Create(key.ToBytes());
        return new ArkAddress(pubKey, serverKey);
    }

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