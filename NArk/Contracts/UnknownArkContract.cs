using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Extensions;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Contracts;

public class UnknownArkContract : ArkContract
{
    private readonly ArkAddress _address;
    private readonly bool? _mainnet;

    public UnknownArkContract(ArkAddress address, OutputDescriptor server, bool? mainnet = null) : base(server)
    {
        try
        {
            _ = address.ToString();
        }
        catch (InvalidOperationException)
        {
            throw new ArgumentNullException(nameof(mainnet),
                "Cannot be null if the specified address does not know its intended network");
        }

        _address = address;
        _mainnet = mainnet;
    }

    public override string Type => ContractType;

    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        throw new InvalidOperationException("Unknown contract cannot be used for signing");
    }

    protected override Dictionary<string, string> GetContractData()
    {
        return new Dictionary<string, string>()
        {
            ["server"] = Server!.ToString(),
            ["address"] = _mainnet is not null ? _address.ToString(_mainnet.Value) : _address.ToString()
        };
    }

    public override ArkAddress GetArkAddress(OutputDescriptor? defaultServerKey = null)
    {
        return _address;
    }

    public const string ContractType = "Unknown";

    public static ArkContract? Parse(Dictionary<string, string> arg1, Network arg2)
    {
        var server = KeyExtensions.ParseOutputDescriptor(arg1["server"], arg2);
        var address = ArkAddress.Parse(arg1["address"]);
        return new UnknownArkContract(address, server, server.Network.ChainName == ChainName.Mainnet);
    }
}