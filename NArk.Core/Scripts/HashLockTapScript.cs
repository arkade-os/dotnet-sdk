using NArk.Abstractions.Scripts;
using NArk.Core.Enums;
using NBitcoin;

namespace NArk.Core.Scripts;

public class HashLockTapScript(byte[] hash, HashLockTypeOption hashLockType, int? preimageSize = null) : ScriptBuilder
{
    public byte[] Hash { get; } = hash;
    public HashLockTypeOption HashLockType { get; } = hashLockType;

    /// <summary>
    /// When set, the preimage must be exactly this many bytes or the script fails before the hash is
    /// even computed.
    /// </summary>
    /// <remarks>
    /// A bare hash lock accepts any preimage that hashes correctly. With HASH160 that includes a
    /// preimage which is itself the 20-byte digest of something else, so a hash lock can be
    /// satisfied by a value the party who chose it never intended as the secret. Pinning the length
    /// closes that off, which is why the VHTLC v2 construction gates every claim-family leaf on 32
    /// bytes.
    /// </remarks>
    public int? PreimageSize { get; } = preimageSize;

    public HashLockTapScript(uint160 hash) :
        this(hash.ToBytes(false), HashLockTypeOption.Hash160)
    { }

    public HashLockTapScript(uint256 hash) :
        this(hash.ToBytes(false), HashLockTypeOption.Sha256)
    { }

    public override IEnumerable<Op> BuildScript()
    {
        if (PreimageSize is { } size)
        {
            yield return OpcodeType.OP_SIZE;
            yield return Op.GetPushOp(size);
            yield return OpcodeType.OP_EQUALVERIFY;
        }

        if (HashLockType == HashLockTypeOption.Hash160)
            yield return OpcodeType.OP_HASH160;
        else
            yield return OpcodeType.OP_SHA256;

        yield return Op.GetPushOp(Hash);
        yield return OpcodeType.OP_EQUAL;
    }
}
