using NArk.Abstractions.Scripts;
using NArk.Core.Helpers;
using NBitcoin;

namespace NArk.Core.Scripts;

public class UnilateralPathArkTapScript(Sequence timeout, NofNMultisigTapScript owners, ScriptBuilder? condition = null)
    : ScriptBuilder
{
    public Sequence Timeout { get; } = timeout;
    public NofNMultisigTapScript OwnersMultiSig { get; } = owners;
    public ScriptBuilder? Condition { get; } = condition;

    public override IEnumerable<Op> BuildScript()
    {
        var condition = Condition?.BuildScript().ToList() ?? [];

        if (condition.Count > 0)
            condition.Add(OpcodeType.OP_VERIFY);

        var multiSigOps = OwnersMultiSig.BuildScript().ToList();

        // OP_CHECKSIGVERIFY => OP_CHECKSIG
        multiSigOps[^1] = OpcodeType.OP_CHECKSIG;

        return [.. condition, Op.GetPushOp(Timeout.Value), OpcodeType.OP_CHECKSEQUENCEVERIFY, OpcodeType.OP_DROP, .. multiSigOps];
    }

    public static UnilateralPathArkTapScript Parse(string hexScript)
    {
        var scriptReader = new ScriptReader(Convert.FromHexString(hexScript));

        List<Op> condition = [];
        while (true)
        {
            var op = scriptReader.Read();
            if (op is null)
            {
                // We re at the end of the script without finding OP_VERIFY
                // so there is no condition
                condition.Clear();
                scriptReader = new ScriptReader(Convert.FromHexString(hexScript));
                break;
            }
            else if (op.Code == OpcodeType.OP_VERIFY)
            {
                break;
            }
            condition.Add(op);
        }

        var sequence = DecodeBip68Sequence(scriptReader.Read());
        if (scriptReader.Read().Code != OpcodeType.OP_CHECKSEQUENCEVERIFY)
            throw new FormatException("Invalid script format: missing OP_CHECKSEQUENCEVERIFY");
        if (scriptReader.Read().Code != OpcodeType.OP_DROP)
            throw new FormatException("Invalid script format: missing OP_DROP");

        return new UnilateralPathArkTapScript(sequence, NofNMultisigTapScript.Parse(scriptReader), new GenericTapScript(condition));
    }

    private static Sequence DecodeBip68Sequence(Op sequenceOp)
    {
        const int sequenceLocktimeGranularity = 9;
        const int sequenceLockTimeIsSeconds = 1 << 22;

        var sequence =
            sequenceOp.IsSmallInt switch
            {
                true when sequenceOp.Code != OpcodeType.OP_0 => (sequenceOp.Code - OpcodeType.OP_1 + 1),
                true => 0,
                false => new CScriptNum(sequenceOp.PushData, true, sequenceOp.PushData.Length).getint()
            };
        var relativeLock = sequence & Sequence.SEQUENCE_LOCKTIME_MASK;

        // Sequence numbers with the most significant bit set are not
        // treated as relative lock-times, nor are they given any
        // consensus-enforced meaning at this point.
        if ((sequence & Sequence.SEQUENCE_LOCKTIME_DISABLE_FLAG) == Sequence.SEQUENCE_LOCKTIME_DISABLE_FLAG)
        {
            throw new FormatException("Invalid sequence: disable flag is set");
        }

        if ((sequence & sequenceLockTimeIsSeconds) != sequenceLockTimeIsSeconds)
            return new Sequence((uint)relativeLock);

        var seconds = relativeLock << sequenceLocktimeGranularity;
        return new Sequence(TimeSpan.FromSeconds(seconds));
    }
}