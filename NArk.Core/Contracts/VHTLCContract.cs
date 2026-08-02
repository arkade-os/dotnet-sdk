using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Core.Contracts;

public class VHTLCContract : ArkContract
{
    public byte[]? Preimage { get; }
    public uint160 Hash { get; }
    public LockTime RefundLocktime { get; }
    public Sequence UnilateralClaimDelay { get; }
    public Sequence UnilateralRefundDelay { get; }
    public Sequence UnilateralRefundWithoutReceiverDelay { get; }

    /// <summary>
    /// Output descriptor for the sender key.
    /// </summary>
    public OutputDescriptor Sender { get; }

    /// <summary>
    /// Output descriptor for the receiver key.
    /// </summary>
    public OutputDescriptor Receiver { get; }

    /// <summary>
    /// Co-signer of the optional covenant-claim leaf, or <c>null</c> for a plain VHTLC.
    /// When set, the contract grows a seventh tapscript leaf letting a third party
    /// claim on the receiver's behalf once the preimage is known — without holding
    /// the receiver's key and without being able to redirect the funds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <em>tweaked</em> key: it is already bound to the destination the
    /// claim is allowed to pay. It is NOT the raw key a covenant signer advertises —
    /// passing that produces a different taproot output key, so the address will not
    /// match what the counterparty funds and nobody will be able to spend the VTXO.
    /// Compute it with the covenant provider that owns the tweak, never by hand.
    /// </para>
    /// <para>
    /// Setting this changes the contract's address. Both sides of a swap must agree
    /// on it before the lockup address is derived.
    /// </para>
    /// </remarks>
    public TaprootPubKey? CovenantClaimKey { get; }

    public VHTLCContract(OutputDescriptor server, OutputDescriptor sender, OutputDescriptor receiver,
        byte[] preimage,
        LockTime refundLocktime,
        Sequence unilateralClaimDelay,
        Sequence unilateralRefundDelay,
        Sequence unilateralRefundWithoutReceiverDelay)
        : this(server, sender, receiver, preimage, refundLocktime,
            unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay,
            covenantClaimKey: null)
    {
    }

    /// <param name="covenantClaimKey">
    /// Optional covenant-claim co-signer; see <see cref="CovenantClaimKey"/>. Pass
    /// <c>null</c> for a plain VHTLC, which builds byte-for-byte the same contract as
    /// the overload without this parameter.
    /// </param>
    public VHTLCContract(OutputDescriptor server, OutputDescriptor sender, OutputDescriptor receiver,
        byte[] preimage,
        LockTime refundLocktime,
        Sequence unilateralClaimDelay,
        Sequence unilateralRefundDelay,
        Sequence unilateralRefundWithoutReceiverDelay,
        TaprootPubKey? covenantClaimKey)
        : this(server, sender, receiver,
            new uint160(Hashes.Hash160(preimage).ToBytes(false)), refundLocktime,
            unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay,
            covenantClaimKey)
    {
        Preimage = preimage;
    }

    public VHTLCContract(OutputDescriptor server, OutputDescriptor sender, OutputDescriptor receiver,
        uint160 hash, LockTime refundLocktime,
        Sequence unilateralClaimDelay,
        Sequence unilateralRefundDelay,
        Sequence unilateralRefundWithoutReceiverDelay)
        : this(server, sender, receiver, hash, refundLocktime,
            unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay,
            covenantClaimKey: null)
    {
    }

    /// <param name="covenantClaimKey">
    /// Optional covenant-claim co-signer; see <see cref="CovenantClaimKey"/>. Pass
    /// <c>null</c> for a plain VHTLC, which builds byte-for-byte the same contract as
    /// the overload without this parameter.
    /// </param>
    public VHTLCContract(OutputDescriptor server, OutputDescriptor sender, OutputDescriptor receiver,
        uint160 hash, LockTime refundLocktime,
        Sequence unilateralClaimDelay,
        Sequence unilateralRefundDelay,
        Sequence unilateralRefundWithoutReceiverDelay,
        TaprootPubKey? covenantClaimKey)
        : base(server)
    {
        if (refundLocktime.Value == 0)
            throw new ArgumentException("refundLocktime must be greater than 0");

        ValidTimeLock(unilateralClaimDelay, nameof(unilateralClaimDelay));
        ValidTimeLock(unilateralRefundDelay, nameof(unilateralRefundDelay));
        ValidTimeLock(unilateralRefundWithoutReceiverDelay, nameof(unilateralRefundWithoutReceiverDelay));

        Sender = sender;
        Receiver = receiver;
        Hash = hash;
        RefundLocktime = refundLocktime;
        UnilateralClaimDelay = unilateralClaimDelay;
        UnilateralRefundDelay = unilateralRefundDelay;
        UnilateralRefundWithoutReceiverDelay = unilateralRefundWithoutReceiverDelay;
        CovenantClaimKey = covenantClaimKey;
    }

    private static void ValidTimeLock(Sequence sequence, string fieldName)
    {
        if (sequence.Value == 0)
            throw new ArgumentException($"{fieldName} timelock must be greater than 0");
        if (sequence.LockType == SequenceLockType.Time && sequence.LockPeriod.TotalSeconds % 512 != 0 || sequence.LockType == SequenceLockType.Time && sequence.LockPeriod.TotalSeconds < 512)
            throw new ArgumentException($"{fieldName} timelock in seconds must be a multiple of 512 and greater than 512");
    }

    public override string Type => ContractType;
    public const string ContractType = "HTLC";

    /// <summary>VHTLC funds live off-chain as a VTXO.</summary>
    public override ContractScope DefaultScope => ContractScope.Offchain;


    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        // VHTLC is a Hashed Timelock Contract VtxoScript implementation
        yield return CreateClaimScript();
        yield return CreateCooperativeScript();
        yield return CreateRefundWithoutReceiverScript();
        yield return CreateUnilateralClaimScript();
        yield return CreateUnilateralRefundScript();
        yield return CreateUnilateralRefundWithoutReceiverScript();

        // Appended last so a plain VHTLC keeps the exact leaf set — and therefore the
        // exact address — it had before this leaf existed.
        if (CovenantClaimKey is not null)
            yield return CreateCovenantClaimScript();
    }

    protected override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            { "server", Server!.ToString() },
            { "sender", Sender.ToString() },
            { "receiver", Receiver.ToString() },
            { "hash", Hash.ToString() },
            { "refundLocktime", RefundLocktime.Value.ToString() },
            { "unilateralClaimDelay", UnilateralClaimDelay.Value.ToString() },
            { "unilateralRefundDelay", UnilateralRefundDelay.Value.ToString() },
            { "unilateralRefundWithoutReceiverDelay", UnilateralRefundWithoutReceiverDelay.Value.ToString() }
        };
        if (Preimage is not null)
            data.Add("preimage", Encoders.Hex.EncodeData(Preimage));
        if (CovenantClaimKey is not null)
            data.Add("covenantClaimKey", Encoders.Hex.EncodeData(CovenantClaimKey.ToBytes()));
        return data;
    }

    public ArkCoin ToCoopRefundCoin(string walletIdentifier, ArkVtxo vtxo)
    {
        if (vtxo.IsSpent())
        {
            throw new InvalidOperationException("Vtxo is already spent");
        }
        return new ArkCoin(walletIdentifier, this, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, Sender,
            CreateCooperativeScript(), null, null, null, vtxo.Swept, vtxo.Unrolled);
    }

    /// <summary>
    /// Creates a VHTLC contract from raw parameters, validating hash byte length and seconds timelocks
    /// before encoding them into NBitcoin types (which lose the original value on rounding).
    /// </summary>
    public static VHTLCContract Create(
        OutputDescriptor server, OutputDescriptor sender, OutputDescriptor receiver,
        byte[] preimageHashBytes, LockTime refundLocktime,
        VHtlcDelay unilateralClaimDelay, VHtlcDelay unilateralRefundDelay, VHtlcDelay unilateralRefundWithoutReceiverDelay)
        => Create(server, sender, receiver, preimageHashBytes, refundLocktime,
            unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay,
            covenantClaimKey: null);

    /// <summary>
    /// Creates a VHTLC contract from raw parameters, validating hash byte length and seconds timelocks
    /// before encoding them into NBitcoin types (which lose the original value on rounding).
    /// </summary>
    /// <param name="covenantClaimKey">
    /// Optional covenant-claim co-signer; see <see cref="CovenantClaimKey"/>. Pass
    /// <c>null</c> for a plain VHTLC.
    /// </param>
    public static VHTLCContract Create(
        OutputDescriptor server, OutputDescriptor sender, OutputDescriptor receiver,
        byte[] preimageHashBytes, LockTime refundLocktime,
        VHtlcDelay unilateralClaimDelay, VHtlcDelay unilateralRefundDelay, VHtlcDelay unilateralRefundWithoutReceiverDelay,
        TaprootPubKey? covenantClaimKey)
    {
        if (preimageHashBytes.Length != 20)
            throw new ArgumentException("preimage hash must be 20 bytes", nameof(preimageHashBytes));
        if (refundLocktime.Value == 0)
            throw new ArgumentException("refund locktime must be greater than 0", nameof(refundLocktime));

        return new VHTLCContract(server, sender, receiver,
            new uint160(preimageHashBytes, false), refundLocktime,
            unilateralClaimDelay.ToSequence(nameof(unilateralClaimDelay)),
            unilateralRefundDelay.ToSequence(nameof(unilateralRefundDelay)),
            unilateralRefundWithoutReceiverDelay.ToSequence(nameof(unilateralRefundWithoutReceiverDelay)),
            covenantClaimKey);
    }

    public static ArkContract? Parse(Dictionary<string, string> contractData, Network network)
    {
        var server = KeyExtensions.ParseOutputDescriptor(contractData["server"], network);
        var senderDescriptor = KeyExtensions.ParseOutputDescriptor(contractData["sender"], network);
        var receiverDescriptor = KeyExtensions.ParseOutputDescriptor(contractData["receiver"], network);
        var hash = new uint160(contractData["hash"]);
        var refundLocktime = new LockTime(uint.Parse(contractData["refundLocktime"]));
        var unilateralClaimDelay = new Sequence(uint.Parse(contractData["unilateralClaimDelay"]));
        var unilateralRefundDelay = new Sequence(uint.Parse(contractData["unilateralRefundDelay"]));
        var unilateralRefundWithoutReceiverDelay = new Sequence(uint.Parse(contractData["unilateralRefundWithoutReceiverDelay"]));

        // Absent on every contract written before covenant claims existed, so its
        // absence must keep parsing to exactly the contract those bytes described.
        var covenantClaimKey = contractData.TryGetValue("covenantClaimKey", out var covenantKeyHex)
            ? ParseCovenantClaimKey(covenantKeyHex)
            : null;

        if (contractData.TryGetValue("preimage", out var preimage))
        {
            var preimageBytes = Convert.FromHexString(preimage);
            if (!hash.ToBytes().SequenceEqual(Hashes.Hash160(preimageBytes).ToBytes(false)))
            {
                throw new FormatException("preimage does not match hash");
            }
            return new VHTLCContract(server, senderDescriptor, receiverDescriptor, preimageBytes, refundLocktime, unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay, covenantClaimKey);
        }

        return new VHTLCContract(server, senderDescriptor, receiverDescriptor, hash, refundLocktime, unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay, covenantClaimKey);
    }

    /// <summary>
    /// Parses a persisted covenant claim key, rejecting anything that is not a valid
    /// x-only point.
    /// </summary>
    /// <remarks>
    /// <see cref="TaprootPubKey"/> only checks the length, so a truncated or corrupted
    /// stored value would survive until <see cref="CreateCovenantClaimScript"/> tried to
    /// use it — surfacing as an opaque failure deep in script building, with nothing
    /// pointing back at the contract row that caused it. Failing here keeps the error
    /// attached to the data that is actually wrong.
    /// </remarks>
    /// <exception cref="FormatException">The value is not a valid x-only public key.</exception>
    private static TaprootPubKey ParseCovenantClaimKey(string hex)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException ex)
        {
            throw new FormatException("covenantClaimKey is not valid hex", ex);
        }

        if (bytes.Length != 32 || !ECXOnlyPubKey.TryCreate(bytes, Context.Instance, out _))
            throw new FormatException(
                $"covenantClaimKey is not a valid x-only public key ({bytes.Length} bytes)");

        return new TaprootPubKey(bytes);
    }

    public ScriptBuilder CreateClaimScript()
    {
        // claim (preimage + receiver)
        var hashLock = new HashLockTapScript(Hash);
        var receiverMultisig = new NofNMultisigTapScript([Receiver.ToXOnlyPubKey()]);
        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(),
            new CompositeTapScript(hashLock, new VerifyTapScript(), receiverMultisig));
    }

    /// <summary>
    /// Covenant claim (preimage + server + covenant co-signer). Emits
    /// <c>OP_HASH160 &lt;hash&gt; OP_EQUAL OP_VERIFY &lt;server&gt; OP_CHECKSIGVERIFY
    /// &lt;covenantClaimKey&gt; OP_CHECKSIG</c>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CreateClaimScript"/> this path does not need the receiver's
    /// signature, which is what lets a third party claim while the receiver is
    /// offline. It is safe because <see cref="CovenantClaimKey"/> is tweaked by the
    /// destination the spend must pay, so the co-signer can only sign a transaction
    /// that sends the funds where the receiver already agreed they go.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No covenant claim key is set.</exception>
    public ScriptBuilder CreateCovenantClaimScript()
    {
        if (CovenantClaimKey is null)
            throw new InvalidOperationException(
                "This VHTLC has no covenant claim key, so it has no covenant claim path.");

        var hashLock = new HashLockTapScript(Hash);
        var serverMultisig = new NofNMultisigTapScript([Server!.ToXOnlyPubKey()]);

        // The covenant key terminates the owner set under OP_CHECKSIG; the server is
        // checked before it with OP_CHECKSIGVERIFY. Order is consensus-critical — it
        // is what the co-signer reproduces when deciding whether it can sign.
        return new CollaborativePathArkTapScript(
            ECXOnlyPubKey.Create(CovenantClaimKey.ToBytes()),
            new CompositeTapScript(hashLock, new VerifyTapScript(), serverMultisig));
    }

    public ScriptBuilder CreateCooperativeScript()
    {
        // refund (sender + receiver + server)
        var senderReceiverMultisig = new NofNMultisigTapScript([Sender.ToXOnlyPubKey(), Receiver.ToXOnlyPubKey()]);
        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(), senderReceiverMultisig);
    }

    public ScriptBuilder CreateRefundWithoutReceiverScript()
    {
        // refundWithoutReceiver (at refundLocktime, sender  + server)
        var senderReceiverMultisig = new NofNMultisigTapScript([Sender.ToXOnlyPubKey()]);
        var lockTime = new LockTimeTapScript(RefundLocktime);
        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(),
            new CompositeTapScript(lockTime, senderReceiverMultisig));
    }


    public ScriptBuilder CreateUnilateralClaimScript()
    {
        // unilateralClaim (preimage + receiver after unilateralClaimDelay)
        var hashLock = new HashLockTapScript(Hash);
        var receiverMultisig = new NofNMultisigTapScript([Receiver.ToXOnlyPubKey()]);
        return new UnilateralPathArkTapScript(UnilateralClaimDelay,
            receiverMultisig, hashLock);
    }

    public ScriptBuilder CreateUnilateralRefundScript()
    {
        // unilateralRefund (sender + receiver after unilateralRefundDelay)
        var senderReceiverMultisig = new NofNMultisigTapScript([Sender.ToXOnlyPubKey(), Receiver.ToXOnlyPubKey()]);
        return new UnilateralPathArkTapScript(UnilateralRefundDelay, senderReceiverMultisig);
    }

    public ScriptBuilder CreateUnilateralRefundWithoutReceiverScript()
    {
        // unilateralRefundWithoutReceiver (sender after unilateralRefundWithoutReceiverDelay)
        return new UnilateralPathArkTapScript(UnilateralRefundWithoutReceiverDelay,
            new NofNMultisigTapScript([Sender.ToXOnlyPubKey()]));
    }
}

/// <summary>A CSV timelock delay for a VHTLC contract, expressed in blocks or seconds.</summary>
public readonly record struct VHtlcDelay(bool IsSeconds, uint Value)
{
    public static VHtlcDelay Blocks(uint blocks) => new(false, blocks);
    public static VHtlcDelay Seconds(uint seconds) => new(true, seconds);

    internal Sequence ToSequence(string fieldName)
    {
        if (Value == 0)
            throw new ArgumentException($"{fieldName} must be greater than 0");
        if (IsSeconds)
        {
            if (Value < 512)
                throw new ArgumentException("seconds timelock must be greater or equal to 512");
            if (Value % 512 != 0)
                throw new ArgumentException("seconds timelock must be a multiple of 512");
            return new Sequence(TimeSpan.FromSeconds(Value));
        }
        return new Sequence(Value);
    }
}
