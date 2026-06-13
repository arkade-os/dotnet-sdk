using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Abstractions;

public class ArkCoin : Coin
{
    public ArkCoin(string walletIdentifier,
        ArkContract contract,
        DateTimeOffset birth,
        DateTimeOffset? expiresAt,
        uint? expiresAtHeight,
        OutPoint outPoint,
        TxOut txOut,
        OutputDescriptor? signerDescriptor,
        ScriptBuilder spendingScriptBuilder,
        WitScript? spendingConditionWitness,
        LockTime? lockTime,
        Sequence? sequence,
        bool swept,
        bool unrolled,
        IReadOnlyList<VtxoAsset>? assets = null) : base(outPoint, txOut)
    {
        //FIXME: every place where this is instantiated, it should check that the coin is unspent
        WalletIdentifier = walletIdentifier;
        Contract = contract;
        Birth = birth;
        ExpiresAt = expiresAt;
        ExpiresAtHeight = expiresAtHeight;
        SignerDescriptor = signerDescriptor;
        SpendingScriptBuilder = spendingScriptBuilder;
        SpendingConditionWitness = spendingConditionWitness;
        LockTime = lockTime;
        Sequence = sequence;
        Swept = swept;
        Unrolled = unrolled;
        Assets = assets;

        if (sequence is null && spendingScriptBuilder.BuildScript().Contains(OpcodeType.OP_CHECKSEQUENCEVERIFY))
        {
            throw new InvalidOperationException("Sequence is required");
        }
    }

    public ArkCoin(ArkCoin other) : this(
        other.WalletIdentifier, other.Contract, other.Birth, other.ExpiresAt, other.ExpiresAtHeight, other.Outpoint.Clone(), other.TxOut.Clone(), other.SignerDescriptor,
        other.SpendingScriptBuilder, other.SpendingConditionWitness?.Clone(), other.LockTime, other.Sequence, other.Swept, other.Unrolled, other.Assets)
    {
    }

    public string WalletIdentifier { get; }
    public ArkContract Contract { get; }
    public DateTimeOffset Birth { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public uint? ExpiresAtHeight { get; }
    public OutputDescriptor? SignerDescriptor { get; }
    public ScriptBuilder SpendingScriptBuilder { get; }
    public WitScript? SpendingConditionWitness { get; }
    public LockTime? LockTime { get; }
    public Sequence? Sequence { get; }
    public bool Swept { get; }
    public bool Unrolled { get; }
    public IReadOnlyList<VtxoAsset>? Assets { get; }

    public TapScript SpendingScript => SpendingScriptBuilder.Build();

    private bool IsExpired(TimeHeight current)
    {
        if (ExpiresAt is not null && current.Timestamp >= ExpiresAt)
            return true;
        if (ExpiresAtHeight is not null && current.Height >= ExpiresAtHeight)
            return true;
        return false;
    }

    public bool CanSpendOffchain(TimeHeight current)
    {
        // Coins can be spent offchain (in Ark protocol) if they are NOT recoverable.
        // Recoverable coins are swept or expired and can only be redeemed onchain.
        return !IsRecoverable(current);
    }

    public bool IsRecoverable(TimeHeight current)
    {
        return Swept || IsExpired(current) ;
    }

    /// <summary>
    /// True when this coin's contract is bound to a deprecated Arkade server signer whose
    /// cooperative-sign cutoff has already passed — the operator will no longer co-sign an offchain
    /// spend, so the coin can only be redeemed once it becomes recoverable (after expiry). A cutoff
    /// of <c>0</c> means "no cutoff" and is never treated as past.
    /// </summary>
    /// <param name="deprecatedSigners">Deprecated signer pubkeys mapped to their cutoff (unix seconds), from <c>ArkServerInfo.DeprecatedSigners</c>.</param>
    /// <param name="nowUnixSeconds">The current time in unix seconds.</param>
    public bool IsDeprecatedSignerPastCutoff(IReadOnlyDictionary<ECXOnlyPubKey, long> deprecatedSigners, long nowUnixSeconds)
    {
        if (Contract.Server is null || deprecatedSigners.Count == 0)
            return false;

        // ECXOnlyPubKey uses reference equality, so compare by the 32-byte x-coordinate.
        var serverKey = Contract.Server.ToXOnlyPubKey().ToBytes();
        foreach (var (key, cutoff) in deprecatedSigners)
        {
            // cutoff 0 == "no cutoff"; a non-zero cutoff at/before now means the operator no longer co-signs.
            if (cutoff != 0 && cutoff <= nowUnixSeconds && key.ToBytes().AsSpan().SequenceEqual(serverKey))
                return true;
        }
        return false;
    }

    /// <summary>
    /// As <see cref="CanSpendOffchain(TimeHeight)"/>, but additionally excludes coins under a
    /// deprecated signer past its cutoff (<see cref="IsDeprecatedSignerPastCutoff"/>): past the
    /// cutoff the operator stops co-signing, so the coin is no longer offchain-spendable even though
    /// it is not yet recoverable.
    /// </summary>
    public bool CanSpendOffchain(TimeHeight current, IReadOnlyDictionary<ECXOnlyPubKey, long> deprecatedSigners)
    {
        return CanSpendOffchain(current)
               && !IsDeprecatedSignerPastCutoff(deprecatedSigners, current.Timestamp.ToUnixTimeSeconds());
    }

    public bool RequiresForfeit()
    {
        return !Swept && !Unrolled;
    }

    public PSBTInput? FillPsbtInput(PSBT psbt)
    {
        var psbtInput = psbt.Inputs.FindIndexedInput(Outpoint);
        if (psbtInput is null)
        {
            return null;
        }

        psbtInput.SetArkFieldTapTree(Contract.GetTapScriptList());
        psbtInput.SetTaprootLeafScript(Contract.GetTaprootSpendInfo(), SpendingScript);
        if (SpendingConditionWitness is not null)
        {
            psbtInput.SetArkFieldConditionWitness(SpendingConditionWitness);
        }

        return psbtInput;
    }

    public double GetRawExpiry()
    {
        if (ExpiresAt is not null)
        {
            return ExpiresAt.Value.ToUnixTimeSeconds();
        }

        if (ExpiresAtHeight is not null)
        {
            return ExpiresAtHeight.Value;
        }

        return 0;
    }
}