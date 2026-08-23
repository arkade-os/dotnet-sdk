using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Contracts;

public class ArkBoardingContract(OutputDescriptor server, Sequence exitDelay, OutputDescriptor userDescriptor)
    : ArkContract(server)
{
    private readonly Sequence _exitDelay = exitDelay;

    /// <summary>
    /// Output descriptor for the user key.
    /// </summary>
    public OutputDescriptor User { get; } = userDescriptor;

    public override string Type => ContractType;
    public const string ContractType = "Boarding";

    /// <summary>Boarding funds live on-chain as a boarding UTXO.</summary>
    public override ContractScope DefaultScope => ContractScope.Onchain;


    /// <summary>
    /// Boarding contracts use on-chain Bitcoin addresses, not Ark addresses.
    /// Use <see cref="GetOnchainAddress"/> instead.
    /// </summary>
    public override ArkAddress GetArkAddress(OutputDescriptor? defaultServerKey = null)
        => throw new InvalidOperationException(
            "Boarding contracts use on-chain Bitcoin addresses. Use GetOnchainAddress(network) instead.");

    /// <summary>
    /// Returns the on-chain P2TR Bitcoin address (bc1p.../tb1p.../bcrt1p...) for this boarding contract.
    /// </summary>
    public BitcoinAddress GetOnchainAddress(Network network)
        => GetTaprootSpendInfo().OutputPubKey.GetAddress(network);

    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [
            CollaborativePath(),
            UnilateralPath()
        ];
    }

    public ScriptBuilder CollaborativePath()
    {
        var ownerScript = new NofNMultisigTapScript([User.ToXOnlyPubKey()]);
        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(), ownerScript);
    }

    public ScriptBuilder UnilateralPath()
    {
        var ownerScript = new NofNMultisigTapScript([User.ToXOnlyPubKey()]);
        return new UnilateralPathArkTapScript(_exitDelay, ownerScript);
    }

    protected override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["exit_delay"] = _exitDelay.Value.ToString(),
            ["user"] = User.ToString(),
            ["server"] = Server!.ToString()
        };
        return data;
    }

    public static ArkContract Parse(Dictionary<string, string> contractData, Network network)
    {
        var server = KeyExtensions.ParseOutputDescriptor(contractData["server"], network);
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var userDescriptor = KeyExtensions.ParseOutputDescriptor(contractData["user"], network);
        return new ArkBoardingContract(server, exitDelay, userDescriptor);
    }
}
