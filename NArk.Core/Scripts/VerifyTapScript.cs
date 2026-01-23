using NArk.Abstractions.Scripts;
using NBitcoin;

namespace NArk.Core.Scripts;

public class VerifyTapScript : ScriptBuilder
{
    public override IEnumerable<Op> BuildScript()
    {
        yield return OpcodeType.OP_VERIFY;
    }
}