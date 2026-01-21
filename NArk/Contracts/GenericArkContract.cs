using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NBitcoin.Scripting;

namespace NArk.Contracts;

public class GenericArkContract(OutputDescriptor server, IEnumerable<ScriptBuilder> scriptBuilders, Dictionary<string, string>? contractData = null) : ArkContract(server)
{
    public override string Type { get; } = "generic";

    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return scriptBuilders;
    }

    protected override Dictionary<string, string> GetContractData()
    {
        return contractData ?? [];
    }
}