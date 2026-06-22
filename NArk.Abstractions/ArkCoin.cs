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

/// <summary>
/// A spendable VTXO with the spending script, contract, and expiry data the SDK needs
/// to participate in a batch or perform an offchain send.
/// </summary>
public class ArkCoin : Coin
{
#pragma warning disable CS1591
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

        if (sequence is null && spendingScriptBuilder.BuildScript().Any(op => op.Code == OpcodeType.OP_CHECKSEQUENCEVERIFY))
        {
            throw new InvalidOperationException("Sequence is required");
        }
    }

    public ArkCoin(ArkCoin other) : this(
        other.WalletIdentifier, other.Contract, other.Birth, other.ExpiresAt, other.ExpiresAtHeight, other.Outpoint.Clone(), other.TxOut.Clone(), other.SignerDescriptor,
        other.SpendingScriptBuilder, other.SpendingConditionWitness?.Clone(), other.LockTime, other.Sequence, other.Swept, other.Unrolled, other.Assets)
    {
    }
#pragma warning restore CS1591

    /// <summary>Owner wallet ID.</summary>
    public string WalletIdentifier { get; }
    /// <summary>The on-chain contract that encumbers this coin.</summary>
    public ArkContract Contract { get; }
    /// <summary>When this coin was first observed.</summary>
    public DateTimeOffset Birth { get; }
    /// <summary>Wall-clock expiry; null when expiry is block-height only.</summary>
    public DateTimeOffset? ExpiresAt { get; }
    /// <summary>Block-height expiry; null when expiry is time-based only.</summary>
    public uint? ExpiresAtHeight { get; }
    /// <summary>Output descriptor used to sign spends of this coin.</summary>
    public OutputDescriptor? SignerDescriptor { get; }
    /// <summary>Builds the tapscript leaf used when spending this coin.</summary>
    public ScriptBuilder SpendingScriptBuilder { get; }
    /// <summary>Additional witness data prepended to the script path witness, when required by the spending condition.</summary>
    public WitScript? SpendingConditionWitness { get; }
    /// <summary>Transaction locktime required when spending, or null.</summary>
    public LockTime? LockTime { get; }
    /// <summary>Input sequence required for OP_CSV leaves; null for non-CSV scripts.</summary>
    public Sequence? Sequence { get; }
    /// <summary>True if the Arkade server has already swept this coin's VTXO on-chain.</summary>
    public bool Swept { get; }
    /// <summary>True if this coin came from the unilateral-exit (unroll) path rather than a batch.</summary>
    public bool Unrolled { get; }
    /// <summary>Ark-issued assets attached to this coin; null for BTC-only coins.</summary>
    public IReadOnlyList<VtxoAsset>? Assets { get; }

    /// <summary>Compiled tapscript leaf for spending.</summary>
    public TapScript SpendingScript => SpendingScriptBuilder.Build();

    private bool IsExpired(TimeHeight current)
    {
        if (ExpiresAt is not null && current.Timestamp >= ExpiresAt)
            return true;
        if (ExpiresAtHeight is not null && current.Height >= ExpiresAtHeight)
            return true;
        return false;
    }

    /// <summary>True when the coin can participate in an offchain Arkade intent (not yet expired or swept).</summary>
    public bool CanSpendOffchain(TimeHeight current)
    {
        // Coins can be spent offchain (in Ark protocol) if they are NOT recoverable.
        // Recoverable coins are swept or expired and can only be redeemed onchain.
        return !IsRecoverable(current);
    }

    /// <summary>True when the coin must be redeemed on-chain (swept or past expiry).</summary>
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

    /// <summary>True when spending this coin offchain requires submitting a forfeit transaction.</summary>
    public bool RequiresForfeit()
    {
        return !Swept && !Unrolled;
    }

    /// <summary>
    /// Populates the PSBT input for this coin with taproot spend-info and condition witness.
    /// Returns null if this coin's outpoint is not in the PSBT.
    /// </summary>
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

    /// <summary>
    /// Returns expiry as a raw number: unix seconds for time-gated, block height for height-gated, 0 if no expiry.
    /// </summary>
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