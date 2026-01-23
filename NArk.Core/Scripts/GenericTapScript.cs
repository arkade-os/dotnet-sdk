using NArk.Abstractions.Scripts;
using NBitcoin;

namespace NArk.Core.Scripts;

public class GenericTapScript(IEnumerable<Op> ops) : ScriptBuilder
{
    public GenericTapScript(TapScript script) : this(script.Script.ToOps())
    {
    }

    public override IEnumerable<Op> BuildScript()
    {
        return ops;
    }
}