using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Core.Contracts;

public class ArkDelegateContract : ArkContract
{
    private readonly Sequence _exitDelay;
    public OutputDescriptor User { get; }
    public OutputDescriptor Delegate { get; }
    public LockTime? CltvLocktime { get; }

    public override string Type => ContractType;
    public const string ContractType = "Delegate";

    /// <summary>Delegate funds live off-chain as a VTXO.</summary>
    public override ContractScope DefaultScope => ContractScope.Offchain;

    public ArkDelegateContract(
        OutputDescriptor server,
        Sequence exitDelay,
        OutputDescriptor user,
        OutputDescriptor @delegate,
        LockTime? cltvLocktime = null)
        : base(server)
    {
        _exitDelay = exitDelay;
        CltvLocktime = cltvLocktime;
        User = user;
        Delegate = @delegate;
    }

    /// <summary>
    /// Leaf ordering follows canonical Ark SDK convention: [delegate, forfeit, exit].
    /// </summary>
    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [
            DelegatePath(),
            CollaborativePath(),
            ExitPath()
        ];
    }

    /// <summary>
    /// Collaborative path: user + server (2-of-2).
    /// </summary>
    public ScriptBuilder CollaborativePath()
    {
        var ownerScript = new NofNMultisigTapScript([User.ToXOnlyPubKey()]);
        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(), ownerScript);
    }

    /// <summary>
    /// Unilateral exit path: user only, after CSV delay.
    /// </summary>
    public ScriptBuilder ExitPath()
    {
        var ownerScript = new NofNMultisigTapScript([User.ToXOnlyPubKey()]);
        return new UnilateralPathArkTapScript(_exitDelay, ownerScript);
    }

    /// <summary>
    /// Delegate path: user + delegate + server (3-of-3).
    /// When <see cref="CltvLocktime"/> is set, the path is gated by an absolute timelock
    /// that prevents the delegate from acting before a certain block height — giving the
    /// owner a safety window to collaboratively spend via the forfeit path.
    /// When null, the delegate can act immediately (plain multisig).
    /// </summary>
    public ScriptBuilder DelegatePath()
    {
        var multisig = new NofNMultisigTapScript([User.ToXOnlyPubKey(), Delegate.ToXOnlyPubKey()]);

        if (CltvLocktime is { } locktime)
        {
            var cltvGate = new LockTimeTapScript(locktime);
            return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(),
                new CompositeTapScript(cltvGate, multisig));
        }

        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(), multisig);
    }

    protected override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["exit_delay"] = _exitDelay.Value.ToString(),
            ["user"] = User.ToString(),
            ["delegate"] = Delegate.ToString(),
            ["server"] = Server!.ToString()
        };

        if (CltvLocktime is { } locktime)
            data["cltv_locktime"] = locktime.Value.ToString();

        return data;
    }

    public static ArkContract Parse(Dictionary<string, string> contractData, Network network)
    {
        var server = KeyExtensions.ParseOutputDescriptor(contractData["server"], network);
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var userDescriptor = KeyExtensions.ParseOutputDescriptor(contractData["user"], network);
        var delegateDescriptor = KeyExtensions.ParseOutputDescriptor(contractData["delegate"], network);

        LockTime? cltvLocktime = contractData.TryGetValue("cltv_locktime", out var cltvStr)
            ? new LockTime(uint.Parse(cltvStr))
            : null;

        return new ArkDelegateContract(server, exitDelay, userDescriptor, delegateDescriptor, cltvLocktime);
    }
}
