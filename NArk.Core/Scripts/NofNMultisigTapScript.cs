using NArk.Abstractions.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Core.Scripts;

public class NofNMultisigTapScript(ECXOnlyPubKey[] owners) : ScriptBuilder
{

    public ECXOnlyPubKey[] Owners { get; } = owners;

    public override IEnumerable<Op> BuildScript()
    {
        foreach (var key in Owners)
        {
            yield return Op.GetPushOp(key.ToBytes());
            yield return OpcodeType.OP_CHECKSIGVERIFY;
        }
    }

    public static NofNMultisigTapScript Parse(ScriptReader scriptReader)
    {
        HashSet<ECXOnlyPubKey> owners = [];
        Op lastOp = Op.GetPushOp(0);
        while (lastOp.Code != OpcodeType.OP_CHECKSIG)
        {
            var push = scriptReader.Read();
            lastOp = scriptReader.Read();
            if (lastOp.Code is OpcodeType.OP_CHECKSIG or OpcodeType.OP_CHECKSIGVERIFY && push.PushData.Length is 32)
            {
                owners.Add(ECXOnlyPubKey.Create(push.PushData));
            }
            else
            {
                throw new FormatException("Invalid script format: unexpected opcode");
            }
        }

        return new NofNMultisigTapScript(owners.ToArray());
    }
}
