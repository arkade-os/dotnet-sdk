using NArk.Abstractions.Scripts;
using NBitcoin;

namespace NArk.Core.Scripts;

public class CompositeTapScript(params ScriptBuilder[] scripts) : ScriptBuilder
{
    public ScriptBuilder[] Scripts { get; } = scripts;

    public override IEnumerable<Op> BuildScript()
    {
        return Scripts.SelectMany(script => script.BuildScript());
    }
}