using System.Buffers.Binary;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Enums;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;

namespace NArk.Contracts;

public class ArkNoteContract(uint amount, byte[] preimage) : ArkContract(null!)
{
    public byte[] Preimage => preimage;
    public byte[] Hash => Hashes.SHA256(preimage);
    public OutPoint Outpoint => new(new uint256(Hash), 0);
    public uint Amount { get; } = amount;

    public override string Type => ContractType;
    public const string ContractType = "arknote";

    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        yield return CreateClaimScript();
    }

    public HashLockTapScript CreateClaimScript()
    {
        return new HashLockTapScript(Hash, HashLockTypeOption.Sha256);
    }

    public override TapScript[] GetTapScriptList()
    {
        //we override to remove the checks.
        var leaves = GetScriptBuilders().ToArray();
        return leaves.Select(x => x.Build()).ToArray();
    }

    protected override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["preimage"] = Encoders.Hex.EncodeData(Preimage),
            ["amount"] = amount.ToString(),
        };
        return data;
    }

    public static ArkContract Parse(Dictionary<string, string> arg, Network network)
    {
        var preimage = Encoders.Hex.DecodeData(arg["preimage"]);
        var amount = uint.Parse(arg["amount"]);
        return new ArkNoteContract(amount, preimage);
    }

    public static ArkNoteContract Parse(string note)
    {
        const int PreimageLength = 32; // 32 bytes for the preimage
        const int ValueLength = 4; // 4 bytes for the value

        if (!note.StartsWith(ContractType))
            throw new ArgumentException("Given string is not a valid ark note");

        var encoder = new Base58Encoder();
        var data = encoder.DecodeData(note[ContractType.Length..]);

        if (data.Length != PreimageLength + ValueLength)
        {
            throw new ArgumentException("Given string is not a valid ark note");
        }

        return new ArkNoteContract(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan()[PreimageLength..]), data[..PreimageLength]);
    }
}