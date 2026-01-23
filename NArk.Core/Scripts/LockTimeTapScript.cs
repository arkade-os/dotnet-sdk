using NArk.Abstractions.Scripts;
using NBitcoin;

namespace NArk.Core.Scripts;

public class LockTimeTapScript(LockTime lockTime) : ScriptBuilder
{
    public LockTime LockTime { get; } = lockTime;

    public override IEnumerable<Op> BuildScript()
    {
        yield return Op.GetPushOp(LockTime.Value);
        yield return OpcodeType.OP_CHECKLOCKTIMEVERIFY;
        yield return OpcodeType.OP_DROP;
    }
}