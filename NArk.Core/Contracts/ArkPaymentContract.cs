using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Core.Extensions;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Contracts;

public class ArkPaymentContract(OutputDescriptor server, Sequence exitDelay, OutputDescriptor userDescriptor)
    : ArkContract(server)
{
    private readonly Sequence _exitDelay = exitDelay;

    /// <summary>
    /// Output descriptor for the user key.
    /// </summary>
    public OutputDescriptor User { get; } = userDescriptor;

    public override string Type => ContractType;
    public const string ContractType = "Payment";


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
        return new ArkPaymentContract(server, exitDelay, userDescriptor);
    }
}
