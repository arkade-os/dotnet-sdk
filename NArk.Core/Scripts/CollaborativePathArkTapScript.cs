using NArk.Abstractions.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Core.Scripts;

public class CollaborativePathArkTapScript(ECXOnlyPubKey server, ScriptBuilder? condition = null) : ScriptBuilder
{
    public ECXOnlyPubKey Server { get; } = server;
    public ScriptBuilder? Condition { get; } = condition;

    public override IEnumerable<Op> BuildScript()
    {
        var condition = Condition?.BuildScript() ?? [];
        foreach (var op in condition)
        {
            yield return op;
        }
        yield return Op.GetPushOp(Server.ToBytes());
        yield return OpcodeType.OP_CHECKSIG;
    }
}