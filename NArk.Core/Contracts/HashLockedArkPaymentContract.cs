using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Core.Enums;
using NArk.Core.Extensions;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Scripting;

namespace NArk.Core.Contracts;

public class HashLockedArkPaymentContract(
    OutputDescriptor server,
    Sequence exitDelay,
    OutputDescriptor user,
    byte[] preimage,
    HashLockTypeOption hashLockType)
    : ArkContract(server)
{
    private readonly Sequence _exitDelay = exitDelay;

    /// <summary>
    /// Output descriptor for the user key.
    /// </summary>
    public OutputDescriptor User { get; } = user;

    public byte[] Hash
    {
        get
        {
            return hashLockType switch
            {
                HashLockTypeOption.Hash160 => Hashes.Hash160(preimage).ToBytes(),
                HashLockTypeOption.Sha256 => Hashes.SHA256(preimage),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }

    public override string Type => ContractType;
    public const string ContractType = "HashLockPaymentContract";
    public byte[] Preimage => preimage;
    public Sequence ExitDelay => _exitDelay;
    public HashLockTypeOption HashLockType => hashLockType;

    protected override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["exit_delay"] = _exitDelay.Value.ToString(),
            ["preimage"] = preimage.ToHexStringLower(),
            ["hash_lock_type"] = Enum.GetName(hashLockType) ?? throw new ArgumentOutOfRangeException(nameof(hashLockType), "Invalid hash lock type")
        };

        data["user"] = User.ToString();

        data["server"] = Server!.ToString();

        return data;
    }

    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [
            CreateClaimScript(),
            UnilateralPath()
        ];
    }

    public ScriptBuilder CreateClaimScript()
    {
        var hashLock = new HashLockTapScript(Hash, hashLockType);
        var receiverMultisig = new NofNMultisigTapScript([User?.ToXOnlyPubKey() ?? throw new InvalidOperationException("User is required for claim script generation")]);
        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(),
            new CompositeTapScript(hashLock, new VerifyTapScript(), receiverMultisig));
    }

    public ScriptBuilder UnilateralPath()
    {
        var ownerScript = new NofNMultisigTapScript([User?.ToXOnlyPubKey() ?? throw new InvalidOperationException("User is required for unilateral script generation")]);
        return new UnilateralPathArkTapScript(_exitDelay, ownerScript);
    }

    public static ArkContract Parse(Dictionary<string, string> contractData, Network network)
    {
        var server = KeyExtensions.ParseOutputDescriptor(contractData["server"], network);
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var userDescriptor = KeyExtensions.ParseOutputDescriptor(contractData["user"], network);
        var preimage = Convert.FromHexString(contractData["preimage"]);
        var hashLockType = Enum.Parse<HashLockTypeOption>(contractData["hash_lock_type"]);
        return new HashLockedArkPaymentContract(server, exitDelay, userDescriptor, preimage, hashLockType);
    }
}
