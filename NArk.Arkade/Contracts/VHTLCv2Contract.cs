using System.Numerics;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Arkade.Crypto;
using NArk.Arkade.Scripts;
using NArk.Core.Contracts;
using NArk.Core.Enums;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Contracts;

/// <summary>
/// The VHTLC the Lightning and off-board swap corridors settle into: the six leaves of the
/// reference VHTLC construction, plus up to two whose co-signer is an emulator key tweaked by a
/// covenant that pins where the spend may pay.
/// </summary>
/// <remarks>
/// <para>
/// This must derive byte-for-byte what the counterparty derives, because the address is the
/// agreement — nothing is exchanged to confirm it. Leaf order decides the taproot merkle root and
/// therefore the address, so the order below is not a stylistic choice: it is the order
/// <c>VHTLC.ScriptV2</c> builds in, and changing it silently produces an address whose funds
/// nobody can spend. <c>NArk.Tests/ArkadeIntents/Fixtures/covenant_swap.json</c> holds vectors
/// generated from that implementation; if this class and those vectors disagree, this class is
/// what is wrong.
/// </para>
/// <para>
/// The ladder is six leaves, seven or eight, depending on which optional covenant leaves are asked
/// for: <see cref="NonInteractiveClaim"/> and <see cref="NonInteractiveRefund"/> are independently
/// optional and append in that order. The RFQ corridors ask for both, which is the eight-leaf shape
/// the fixtures pin — but a contract built with a different set is a different address, not a
/// variant of the same one, so the set is part of what the two sides must agree on.
/// <c>VHTLC.Options</c>'s ninth leaf, <c>nonInteractiveRefundWithoutReceiver</c>, is not modelled
/// here yet; see <see cref="VHTLCv2NonInteractiveRefund"/>.
/// </para>
/// <para>
/// Roles are positional, not fixed to a party. On <c>arkade:BTC-&gt;lightning:BTC</c> the trader is
/// <see cref="Sender"/> and the solver is <see cref="Receiver"/>; on
/// <c>lightning:BTC-&gt;arkade:BTC</c> they swap, because there it is the solver that funds and the
/// trader that claims. Both corridors build this same class.
/// </para>
/// </remarks>
public class VHTLCv2Contract : ArkContract
{
    /// <summary>Every claim-family leaf gates the preimage to this length before hashing it.</summary>
    public const int PreimageSize = 32;

    /// <summary>The largest asset group index the covenant's introspection opcodes accept.</summary>
    public const int MaxAssetGroupIndex = 0xffff;

    private readonly ECXOnlyPubKey? _nonInteractiveClaimCovenantKey;
    private readonly ECXOnlyPubKey? _nonInteractiveRefundCovenantKey;

    /// <summary>The party that funds the contract, and that the refund paths pay back.</summary>
    public OutputDescriptor Sender { get; }

    /// <summary>The party that claims by revealing the preimage.</summary>
    public OutputDescriptor Receiver { get; }

    /// <summary>HASH160 of the preimage.</summary>
    public uint160 Hash { get; }

    /// <summary>
    /// The secret this contract's claim leaves open, when we hold it — otherwise <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not part of the script: every leaf commits to <see cref="Hash"/>, so a contract with the
    /// preimage and one without derive the same address. It rides along because the claim is the
    /// contract's own operation — anything holding the rebuilt contract can spend it, without a
    /// second lookup into whatever negotiated the swap. <see cref="VHTLCContract"/> carries it the
    /// same way and for the same reason.
    /// </para>
    /// <para>
    /// It is a SECRET travelling in a structure that is otherwise a public identifier: it is written
    /// into the <c>arkcontract=</c> descriptor and the persisted contract row, so it reaches wherever
    /// those are copied. Leave it null on a contract built only to derive or verify an address.
    /// </para>
    /// </remarks>
    public byte[]? Preimage { get; }

    /// <summary>When the sender's timelocked refund path opens.</summary>
    public LockTime RefundLocktime { get; }

    /// <summary>How long after funding the receiver may claim without the server.</summary>
    public Sequence UnilateralClaimDelay { get; }

    /// <summary>How long after funding sender and receiver may refund without the server.</summary>
    public Sequence UnilateralRefundDelay { get; }

    /// <summary>How long after funding the sender alone may refund, needing nobody.</summary>
    public Sequence UnilateralRefundWithoutReceiverDelay { get; }

    /// <summary>The non-interactive claim leaf, or <c>null</c> if this contract carries none.</summary>
    public VHTLCv2NonInteractiveClaim? NonInteractiveClaim { get; }

    /// <summary>The non-interactive refund leaf, or <c>null</c> if this contract carries none.</summary>
    public VHTLCv2NonInteractiveRefund? NonInteractiveRefund { get; }

    /// <summary>
    /// The Arkade asset the covenant leaves bind, or <c>null</c> for a sat-only contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the non-interactive leaves change. Every other leaf is a signature path that asserts
    /// nothing about value, which is why this reaches exactly the leaves whose covenant the
    /// emulator enforces — and why naming an asset without either of them is refused rather than
    /// silently dropped.
    /// </para>
    /// <para>
    /// The sat clause is RETAINED, not replaced: an asset-carrying VTXO carries sats too, so
    /// dropping it would let a spend satisfy the asset covenant while stripping the sats. And ONE
    /// asset is bound: fund this contract with the asset it names and nothing else, because any
    /// additional asset on the VTXO is the spender's to direct.
    /// </para>
    /// </remarks>
    public VHTLCv2Asset? Asset { get; }

    /// <summary>
    /// Builds the contract and, with it, the address both sides must agree on.
    /// </summary>
    /// <param name="server">The Arkade server's key, co-signing every collaborative leaf.</param>
    /// <param name="sender">The party that funds, and that the refund paths pay back.</param>
    /// <param name="receiver">The party that claims by revealing the preimage.</param>
    /// <param name="hash">HASH160 of the preimage.</param>
    /// <param name="refundLocktime">When the sender's timelocked refund path opens.</param>
    /// <param name="unilateralClaimDelay">How long after funding the receiver may claim alone.</param>
    /// <param name="unilateralRefundDelay">How long after funding sender and receiver may refund without the server.</param>
    /// <param name="unilateralRefundWithoutReceiverDelay">How long after funding the sender alone may refund.</param>
    /// <param name="nonInteractiveClaim">The optional covenant claim leaf.</param>
    /// <param name="nonInteractiveRefund">The optional covenant refund leaf, and its optional timelocked twin.</param>
    /// <param name="asset">The Arkade asset the covenant leaves bind; requires at least one of them.</param>
    /// <param name="preimage">
    /// The secret behind <paramref name="hash"/>, when the caller holds it. Changes nothing about the
    /// address; see <see cref="Preimage"/> for what carrying it costs.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A delay is not a relative timelock, a pinned destination is not a P2TR scriptPubKey, an asset
    /// is named that no leaf would bind, a strict claim bound is incomplete or non-positive, or the
    /// preimage is the wrong length or does not hash to <paramref name="hash"/>.
    /// </exception>
    public VHTLCv2Contract(
        OutputDescriptor server,
        OutputDescriptor sender,
        OutputDescriptor receiver,
        uint160 hash,
        LockTime refundLocktime,
        Sequence unilateralClaimDelay,
        Sequence unilateralRefundDelay,
        Sequence unilateralRefundWithoutReceiverDelay,
        VHTLCv2NonInteractiveClaim? nonInteractiveClaim = null,
        VHTLCv2NonInteractiveRefund? nonInteractiveRefund = null,
        VHTLCv2Asset? asset = null,
        byte[]? preimage = null) : base(server)
    {
        ValidateTimeLock(unilateralClaimDelay, nameof(unilateralClaimDelay));
        ValidateTimeLock(unilateralRefundDelay, nameof(unilateralRefundDelay));
        ValidateTimeLock(unilateralRefundWithoutReceiverDelay, nameof(unilateralRefundWithoutReceiverDelay));
        ValidateCovenantOptions(nonInteractiveClaim, nonInteractiveRefund, asset);
        if (preimage is not null)
        {
            // Checked here rather than at claim time. A preimage that does not open this contract is
            // a wrong secret carried by a contract that looks complete, and the first thing it would
            // do is build a spend that fails in the mempool with the attempt already broadcast.
            ValidatePreimage(preimage, hash, nameof(preimage));
        }

        Sender = sender;
        Receiver = receiver;
        Hash = hash;
        RefundLocktime = refundLocktime;
        UnilateralClaimDelay = unilateralClaimDelay;
        UnilateralRefundDelay = unilateralRefundDelay;
        UnilateralRefundWithoutReceiverDelay = unilateralRefundWithoutReceiverDelay;
        NonInteractiveClaim = nonInteractiveClaim;
        NonInteractiveRefund = nonInteractiveRefund;
        Asset = asset;
        Preimage = preimage;

        // Derived once each, in the constructor, rather than on every leaf build.
        _nonInteractiveClaimCovenantKey = nonInteractiveClaim is null
            ? null
            : CovenantKey(
                nonInteractiveClaim.EmulatorPubKey,
                EnforcePayTo(nonInteractiveClaim.ReceiverPkScript, asset, nonInteractiveClaim.Strict));
        _nonInteractiveRefundCovenantKey = nonInteractiveRefund is null
            ? null
            : CovenantKey(
                nonInteractiveRefund.EmulatorPubKey,
                EnforcePayTo(nonInteractiveRefund.SenderPkScript, asset));
    }

    /// <inheritdoc />
    public override string Type => ContractType;

    /// <summary>The discriminator this contract serializes under.</summary>
    public const string ContractType = "HTLCv2";

    /// <inheritdoc />
    public override ContractScope DefaultScope => ContractScope.Offchain;

    /// <summary>
    /// The leaves, in the order that fixes the merkle root. Do not reorder, and do not move an
    /// optional leaf ahead of a mandatory one: every earlier leaf must keep its position.
    /// </summary>
    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        yield return CreateClaimScript();
        yield return CreateRefundScript();
        yield return CreateRefundWithoutReceiverScript();
        yield return CreateUnilateralClaimScript();
        yield return CreateUnilateralRefundScript();
        yield return CreateUnilateralRefundWithoutReceiverScript();
        if (NonInteractiveClaim is not null)
        {
            yield return CreateNonInteractiveClaimScript();
        }
        if (NonInteractiveRefund is not null)
        {
            yield return CreateNonInteractiveRefundScript();
        }
    }

    /// <summary>Preimage + receiver + server.</summary>
    public ScriptBuilder CreateClaimScript() =>
        new CollaborativePathArkTapScript(
            ServerKey,
            new CompositeTapScript(PreimageCondition(), new VerifyTapScript(),
                new NofNMultisigTapScript([ReceiverKey])));

    /// <summary>Sender + receiver + server, immediate — everyone agreeing to cancel now.</summary>
    public ScriptBuilder CreateRefundScript() =>
        new CollaborativePathArkTapScript(ServerKey, new NofNMultisigTapScript([SenderKey, ReceiverKey]));

    /// <summary>Sender + server after <see cref="RefundLocktime"/>, no receiver.</summary>
    public ScriptBuilder CreateRefundWithoutReceiverScript() =>
        new CollaborativePathArkTapScript(
            ServerKey,
            new CompositeTapScript(new LockTimeTapScript(RefundLocktime), new NofNMultisigTapScript([SenderKey])));

    /// <summary>Preimage + receiver alone, after a delay.</summary>
    public ScriptBuilder CreateUnilateralClaimScript() =>
        new UnilateralPathArkTapScript(
            UnilateralClaimDelay, new NofNMultisigTapScript([ReceiverKey]), PreimageCondition());

    /// <summary>Sender + receiver after a delay, no server.</summary>
    public ScriptBuilder CreateUnilateralRefundScript() =>
        new UnilateralPathArkTapScript(UnilateralRefundDelay, new NofNMultisigTapScript([SenderKey, ReceiverKey]));

    /// <summary>The sender alone after the longest delay — the recourse that depends on nobody.</summary>
    public ScriptBuilder CreateUnilateralRefundWithoutReceiverScript() =>
        new UnilateralPathArkTapScript(
            UnilateralRefundWithoutReceiverDelay, new NofNMultisigTapScript([SenderKey]));

    /// <summary>
    /// Preimage + server + the covenant key, pinned to the receiver's own payout. Lets the
    /// receiver's claim be pushed while the receiver is offline.
    /// </summary>
    /// <exception cref="InvalidOperationException">This contract carries no non-interactive claim leaf.</exception>
    public ScriptBuilder CreateNonInteractiveClaimScript() =>
        new CollaborativePathArkTapScript(
            _nonInteractiveClaimCovenantKey
                ?? throw new InvalidOperationException("this VHTLC has no non-interactive claim leaf"),
            new CompositeTapScript(PreimageCondition(), new VerifyTapScript(),
                new NofNMultisigTapScript([ServerKey])));

    /// <summary>
    /// Server + receiver + the covenant key, pinned to the sender's own payout. Releases the refund
    /// the moment those two agree the swap failed, without waiting out <see cref="RefundLocktime"/>
    /// and without a sender signature.
    /// </summary>
    /// <exception cref="InvalidOperationException">This contract carries no non-interactive refund leaf.</exception>
    public ScriptBuilder CreateNonInteractiveRefundScript() =>
        new CollaborativePathArkTapScript(
            NonInteractiveRefundCovenantKey,
            new NofNMultisigTapScript([ServerKey, ReceiverKey]));

    /// <summary>
    /// The ArkadeScript the non-interactive claim leaf's key commits to, which the emulator runs
    /// before it will co-sign.
    /// </summary>
    /// <exception cref="InvalidOperationException">This contract carries no non-interactive claim leaf.</exception>
    public byte[] NonInteractiveClaimArkadeScript =>
        NonInteractiveClaim is { } claim
            ? EnforcePayTo(claim.ReceiverPkScript, Asset, claim.Strict)
            : throw new InvalidOperationException("this VHTLC has no non-interactive claim leaf");

    /// <summary>
    /// The ArkadeScript the non-interactive refund leaf's key commits to.
    /// </summary>
    /// <exception cref="InvalidOperationException">This contract carries no non-interactive refund leaf.</exception>
    public byte[] NonInteractiveRefundArkadeScript =>
        NonInteractiveRefund is { } refund
            ? EnforcePayTo(refund.SenderPkScript, Asset)
            : throw new InvalidOperationException("this VHTLC has no non-interactive refund leaf");

    /// <summary>
    /// The covenant a leaf's key commits to: "the output at this input's index pays
    /// <paramref name="destinationPkScript"/>, for at least what the input was worth" — plus, when
    /// <paramref name="asset"/> is named, the same conservation bound on that asset, and plus the
    /// quoted floors when <paramref name="strict"/> asks for them.
    /// </summary>
    /// <param name="destinationPkScript">The pinned payout — a P2TR scriptPubKey, 34 bytes.</param>
    /// <param name="asset">The asset to bind, or <c>null</c> for the sat-only covenant.</param>
    /// <param name="strict">The opt-in quoted bound. Only the claim leaf ever carries one.</param>
    /// <returns>The ArkadeScript bytes.</returns>
    /// <remarks>
    /// <c>PUSHCURRENTINPUTINDEX</c> as the output index is not an assumption about how a batch pairs
    /// inputs with outputs — the covenant imposes the pairing on the spender. Whatever index a
    /// spending transaction places this input at, the output at that same index must pay the
    /// destination, or the script fails and the emulator never signs. Index alignment is therefore a
    /// liveness obligation on whoever assembles the spend, never a safety assumption.
    /// </remarks>
    public static byte[] EnforcePayTo(
        byte[] destinationPkScript,
        VHTLCv2Asset? asset = null,
        VHTLCv2StrictClaim? strict = null)
    {
        ValidateP2trPkScript(destinationPkScript, nameof(destinationPkScript));

        if (asset is null)
        {
            return ArkadeScript.Encode(SatClause(destinationPkScript, strict?.Amount));
        }

        ValidateAsset(asset, nameof(asset));
        return ArkadeScript.Encode(AssetClause(destinationPkScript, asset, strict));
    }

    /// <summary>
    /// The emulator key tweaked by a covenant, which is what makes the emulator's signature
    /// conditional on the spend actually honouring that covenant.
    /// </summary>
    /// <param name="emulatorPubKey">The emulator's untweaked key.</param>
    /// <param name="arkadeScript">The covenant the key commits to.</param>
    /// <returns>The tweaked co-signer key a leaf commits to.</returns>
    public static ECXOnlyPubKey CovenantKey(ECXOnlyPubKey emulatorPubKey, byte[] arkadeScript) =>
        ECXOnlyPubKey.Create(
            ArkadeTweak.Tweak(new TaprootPubKey(emulatorPubKey.ToBytes()), arkadeScript).ToBytes());

    /// <summary>The sender's x-only key, as the leaves commit to it.</summary>
    public ECXOnlyPubKey SenderKey => Sender.ToXOnlyPubKey();

    /// <summary>The receiver's x-only key, as the leaves commit to it.</summary>
    public ECXOnlyPubKey ReceiverKey => Receiver.ToXOnlyPubKey();

    private ECXOnlyPubKey ServerKey =>
        Server?.ToXOnlyPubKey() ?? throw new InvalidOperationException("Server key is required");

    private ECXOnlyPubKey NonInteractiveRefundCovenantKey =>
        _nonInteractiveRefundCovenantKey
        ?? throw new InvalidOperationException("this VHTLC has no non-interactive refund leaf");

    private HashLockTapScript PreimageCondition() =>
        new(Hash.ToBytes(false), HashLockTypeOption.Hash160, PreimageSize);

    /// <summary>
    /// The sat half of every covenant: the output at this input's index is P2TR, pays the
    /// destination, and carries at least the input's value.
    /// </summary>
    /// <remarks>
    /// One copy, shared by the sat-only and asset covenants — an option added to one and forgotten
    /// on the other would otherwise diverge silently.
    /// </remarks>
    private static IEnumerable<Op> SatClause(byte[] destinationPkScript, long? quotedSats)
    {
        yield return (OpcodeType)ArkadeOpcode.OP_PUSHCURRENTINPUTINDEX;
        yield return OpcodeType.OP_DUP;
        yield return (OpcodeType)ArkadeOpcode.OP_INSPECTOUTPUTSCRIPTPUBKEY;
        yield return PushNumber(1);
        yield return OpcodeType.OP_EQUALVERIFY;
        // The covenant commits to the 32-byte key alone; the 0x5120 P2TR prefix is re-added by the
        // introspection opcode that reads the output script back.
        yield return Op.GetPushOp(destinationPkScript[2..]);
        yield return OpcodeType.OP_EQUALVERIFY;
        yield return (OpcodeType)ArkadeOpcode.OP_INSPECTOUTPUTVALUE;
        if (quotedSats is { } sats)
        {
            // DUP because the output value is needed twice: once against the quote, once against
            // the input.
            yield return OpcodeType.OP_DUP;
            yield return PushNumber(sats);
            yield return OpcodeType.OP_GREATERTHANOREQUAL;
            yield return OpcodeType.OP_VERIFY;
        }
        yield return (OpcodeType)ArkadeOpcode.OP_PUSHCURRENTINPUTINDEX;
        yield return (OpcodeType)ArkadeOpcode.OP_INSPECTINPUTVALUE;
        yield return OpcodeType.OP_GREATERTHANOREQUAL;
    }

    /// <summary>The asset covenant: the asset bound first, then the sat clause verbatim.</summary>
    private static IEnumerable<Op> AssetClause(
        byte[] destinationPkScript, VHTLCv2Asset asset, VHTLCv2StrictClaim? strict)
    {
        // REVERSED, once, here. GenesisTxid is the id in CANONICAL order; the introspection opcodes
        // match against WIRE order, which is those bytes reversed back. Push the canonical bytes
        // unflipped and the lookup reports the asset ABSENT, so the covenant fails and the contract
        // it guards is unspendable — with nothing in the emulator's "OP_VERIFY failed" saying so.
        // A copy rather than an in-place reverse: the caller's id is theirs.
        var inspectionTxid = asset.GenesisTxid.Reverse().ToArray();

        // The output carries at least as much of the asset as the input did, at the same index the
        // sat clause pairs on.
        yield return (OpcodeType)ArkadeOpcode.OP_PUSHCURRENTINPUTINDEX;
        yield return Op.GetPushOp(inspectionTxid);
        yield return PushNumber(asset.GroupIndex);
        yield return (OpcodeType)ArkadeOpcode.OP_INSPECTOUTASSETLOOKUP;
        yield return OpcodeType.OP_VERIFY;   // PRESENT on the output, not merely "zero of it"
        if (strict?.AssetAmount is { } quotedAsset)
        {
            yield return OpcodeType.OP_DUP;
            yield return PushNumber(quotedAsset);
            yield return OpcodeType.OP_GREATERTHANOREQUAL;
            yield return OpcodeType.OP_VERIFY;
        }
        yield return (OpcodeType)ArkadeOpcode.OP_PUSHCURRENTINPUTINDEX;
        yield return Op.GetPushOp(inspectionTxid);
        yield return PushNumber(asset.GroupIndex);
        yield return (OpcodeType)ArkadeOpcode.OP_INSPECTINASSETLOOKUP;
        yield return OpcodeType.OP_VERIFY;   // ...and on the input, so the comparison means something
        yield return OpcodeType.OP_GREATERTHANOREQUAL;
        yield return OpcodeType.OP_VERIFY;
        // Exactly one asset out: nothing injected alongside the one bound.
        yield return (OpcodeType)ArkadeOpcode.OP_PUSHCURRENTINPUTINDEX;
        yield return (OpcodeType)ArkadeOpcode.OP_INSPECTOUTASSETCOUNT;
        yield return PushNumber(1);
        yield return OpcodeType.OP_EQUALVERIFY;

        foreach (var op in SatClause(destinationPkScript, strict?.Amount))
        {
            yield return op;
        }
    }

    /// <summary>
    /// A numeric push under the emulator's MINIMALDATA rule: script-number bytes, collapsed to the
    /// single-byte <c>OP_N</c> forms where one exists.
    /// </summary>
    private static Op PushNumber(BigInteger value)
    {
        var encoded = ArkadeScriptNum.Encode(value);
        return encoded switch
        {
            [] => OpcodeType.OP_0,
            [>= 1 and <= 16] => (OpcodeType)((byte)OpcodeType.OP_1 - 1 + encoded[0]),
            [0x81] => OpcodeType.OP_1NEGATE,
            _ => Op.GetPushOp(encoded),
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            { "type", ContractType },
            { "server", Server?.ToString() ?? string.Empty },
            { "sender", Sender.ToString() },
            { "receiver", Receiver.ToString() },
            { "hash", Hash.ToString() },
            { "refundLocktime", RefundLocktime.Value.ToString() },
            { "unilateralClaimDelay", UnilateralClaimDelay.Value.ToString() },
            { "unilateralRefundDelay", UnilateralRefundDelay.Value.ToString() },
            { "unilateralRefundWithoutReceiverDelay", UnilateralRefundWithoutReceiverDelay.Value.ToString() },
        };

        // Every optional key below is written only when set. A key whose value is the string
        // "undefined" or "false" would round-trip into a contract that derives a DIFFERENT script
        // from the one the row was written against — the same silent drop that makes a half-written
        // covenant pair unreadable, one value further in.
        if (NonInteractiveClaim is { } claim)
        {
            data["niClaimPkScript"] = Convert.ToHexString(claim.ReceiverPkScript).ToLowerInvariant();
            data["niClaimEmulator"] = Convert.ToHexString(claim.EmulatorPubKey.ToBytes()).ToLowerInvariant();
            if (claim.Strict is { } strict)
            {
                data["strictClaimAmount"] = strict.Amount.ToString();
                if (strict.AssetAmount is { } assetAmount)
                {
                    data["strictClaimAssetAmount"] = assetAmount.ToString();
                }
            }
        }

        if (NonInteractiveRefund is { } refund)
        {
            data["niRefundPkScript"] = Convert.ToHexString(refund.SenderPkScript).ToLowerInvariant();
            data["niRefundEmulator"] = Convert.ToHexString(refund.EmulatorPubKey.ToBytes()).ToLowerInvariant();
        }

        if (Asset is { } asset)
        {
            data["assetTxid"] = Convert.ToHexString(asset.GenesisTxid).ToLowerInvariant();
            data["assetGroupIndex"] = asset.GroupIndex.ToString();
        }

        // Written only when held, exactly as <see cref="VHTLCContract"/> writes it: a contract
        // rebuilt from a row that never had one is not a lesser contract, it is the same contract
        // seen by someone who cannot claim it.
        if (Preimage is { } preimage)
        {
            data["preimage"] = Convert.ToHexString(preimage).ToLowerInvariant();
        }

        return data;
    }

    /// <summary>Rebuild a contract from the data <see cref="GetContractData"/> wrote.</summary>
    /// <param name="contractData">The parsed <c>arkcontract=</c> fields.</param>
    /// <param name="network">The network the descriptors belong to.</param>
    /// <returns>The reconstructed contract.</returns>
    /// <exception cref="FormatException">
    /// A covenant leaf, a strict bound or an asset is only half-written, or a flag names a leaf the
    /// row does not carry. Reading any of those as "not set" would rebuild a different script, and
    /// the address it derives would not be the one the row was written for.
    /// </exception>
    public static ArkContract Parse(Dictionary<string, string> contractData, Network network)
    {
        var claimPkScript = contractData.GetValueOrDefault("niClaimPkScript");
        var claimEmulator = contractData.GetValueOrDefault("niClaimEmulator");
        RequireBothOrNeither(claimPkScript, claimEmulator, "niClaimPkScript", "niClaimEmulator");

        var refundPkScript = contractData.GetValueOrDefault("niRefundPkScript");
        var refundEmulator = contractData.GetValueOrDefault("niRefundEmulator");
        RequireBothOrNeither(refundPkScript, refundEmulator, "niRefundPkScript", "niRefundEmulator");

        var assetTxid = contractData.GetValueOrDefault("assetTxid");
        var assetGroupIndex = contractData.GetValueOrDefault("assetGroupIndex");
        RequireBothOrNeither(assetTxid, assetGroupIndex, "assetTxid", "assetGroupIndex");

        var strictAmount = contractData.GetValueOrDefault("strictClaimAmount");
        var strictAssetAmount = contractData.GetValueOrDefault("strictClaimAssetAmount");
        if (strictAssetAmount is not null && strictAmount is null)
        {
            throw new FormatException(
                "'strictClaimAssetAmount' without 'strictClaimAmount': reading this as 'not strict' " +
                "would rebuild the default claim covenant, which is weaker than the row asked for");
        }
        if (strictAmount is not null && claimPkScript is null)
        {
            throw new FormatException(
                "'strictClaimAmount' without the 'niClaim*' keys it bounds: reading this as 'not set' " +
                "would rebuild a script without that bound");
        }

        return new VHTLCv2Contract(
            KeyExtensions.ParseOutputDescriptor(contractData["server"], network),
            KeyExtensions.ParseOutputDescriptor(contractData["sender"], network),
            KeyExtensions.ParseOutputDescriptor(contractData["receiver"], network),
            new uint160(contractData["hash"]),
            new LockTime(uint.Parse(contractData["refundLocktime"])),
            new Sequence(uint.Parse(contractData["unilateralClaimDelay"])),
            new Sequence(uint.Parse(contractData["unilateralRefundDelay"])),
            new Sequence(uint.Parse(contractData["unilateralRefundWithoutReceiverDelay"])),
            claimPkScript is null
                ? null
                : new VHTLCv2NonInteractiveClaim(
                    Convert.FromHexString(claimPkScript),
                    ECXOnlyPubKey.Create(Convert.FromHexString(claimEmulator!)),
                    strictAmount is null
                        ? null
                        : new VHTLCv2StrictClaim(
                            long.Parse(strictAmount),
                            strictAssetAmount is null ? null : long.Parse(strictAssetAmount))),
            refundPkScript is null
                ? null
                : new VHTLCv2NonInteractiveRefund(
                    Convert.FromHexString(refundPkScript),
                    ECXOnlyPubKey.Create(Convert.FromHexString(refundEmulator!))),
            assetTxid is null
                ? null
                : new VHTLCv2Asset(Convert.FromHexString(assetTxid), int.Parse(assetGroupIndex!)),
            contractData.TryGetValue("preimage", out var preimageHex)
                ? Convert.FromHexString(preimageHex)
                : null);
    }

    /// <summary>
    /// The receiver's payout: the <c>claim</c> leaf, opened by revealing the preimage.
    /// </summary>
    /// <param name="walletIdentifier">The wallet holding the receiver's key.</param>
    /// <param name="vtxo">The unspent lockup output.</param>
    /// <param name="preimage">The 32-byte secret whose hash this contract commits to.</param>
    /// <returns>A coin spendable through the claim leaf.</returns>
    /// <exception cref="ArgumentException">The preimage is the wrong length, or hashes to something else.</exception>
    /// <remarks>
    /// Spending this publishes the preimage in the witness, which is what settles the other side of
    /// the swap — so claiming is not merely taking delivery, it is also how the counterparty gets
    /// paid. The preimage is checked against <see cref="Hash"/> before a coin is built: a spend
    /// carrying the wrong one fails in the mempool, having already broadcast the attempt.
    /// </remarks>
    public ArkCoin ToClaimCoin(string walletIdentifier, ArkVtxo vtxo, byte[] preimage)
    {
        if (vtxo.IsSpent())
        {
            throw new InvalidOperationException("the lockup VTXO is already spent");
        }
        ValidatePreimage(preimage, Hash, nameof(preimage));

        return new ArkCoin(
            walletIdentifier, this, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut, Receiver,
            CreateClaimScript(), new WitScript(Op.GetPushOp(preimage)), null, null,
            vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);
    }

    /// <summary>
    /// The receiver's payout, using the preimage this contract already carries.
    /// </summary>
    /// <param name="walletIdentifier">The wallet holding the receiver's key.</param>
    /// <param name="vtxo">The unspent lockup output.</param>
    /// <returns>A coin spendable through the claim leaf.</returns>
    /// <exception cref="InvalidOperationException">
    /// This contract carries no <see cref="Preimage"/>, so it cannot be claimed from alone — the
    /// secret has to come from whoever holds it.
    /// </exception>
    public ArkCoin ToClaimCoin(string walletIdentifier, ArkVtxo vtxo) =>
        ToClaimCoin(
            walletIdentifier, vtxo,
            Preimage ?? throw new InvalidOperationException(
                "this contract carries no preimage, so the claim's secret must be passed in"));

    /// <summary>
    /// The sender's own timelocked way out: <c>refundWithoutReceiver</c>, co-signed by the Arkade
    /// server once <see cref="RefundLocktime"/> has passed.
    /// </summary>
    /// <param name="walletIdentifier">The wallet holding the sender's key.</param>
    /// <param name="vtxo">The unspent lockup output.</param>
    /// <returns>A coin spendable through that leaf.</returns>
    /// <remarks>
    /// This is the sender's recourse that needs no counterparty, and the only leaf a sender-side
    /// wallet can build alone before the CSV ladder opens. The neighbouring
    /// <c>nonInteractiveRefund</c> leaf resolves without waiting out the locktime, but it is not an
    /// alternative here: it is co-signed by the emulator against a covenant, so it is pushed on the
    /// sender's behalf rather than built by this wallet, which holds neither key. Unlike it this
    /// leaf carries no covenant, so the payout is not pinned — the caller chooses where it goes.
    /// </remarks>
    public ArkCoin ToRefundWithoutReceiverCoin(string walletIdentifier, ArkVtxo vtxo)
    {
        if (vtxo.IsSpent())
        {
            throw new InvalidOperationException("the lockup VTXO is already spent");
        }

        return new ArkCoin(
            walletIdentifier, this, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut, Sender,
            CreateRefundWithoutReceiverScript(), null, RefundLocktime, null,
            vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);
    }

    private static void ValidatePreimage(byte[] preimage, uint160 hash, string name)
    {
        if (preimage.Length != PreimageSize)
        {
            throw new ArgumentException(
                $"preimage must be {PreimageSize} bytes, got {preimage.Length}", name);
        }

        var digest = NBitcoin.Crypto.Hashes.RIPEMD160(NBitcoin.Crypto.Hashes.SHA256(preimage), 32);
        if (!digest.SequenceEqual(hash.ToBytes(false)))
        {
            throw new ArgumentException(
                "this preimage does not hash to the contract's committed digest", name);
        }
    }

    private static void ValidateTimeLock(Sequence sequence, string name)
    {
        if (!sequence.IsRelativeLock)
        {
            throw new ArgumentException($"{name} must be a relative timelock", name);
        }
    }

    private static void ValidateCovenantOptions(
        VHTLCv2NonInteractiveClaim? claim,
        VHTLCv2NonInteractiveRefund? refund,
        VHTLCv2Asset? asset)
    {
        if (claim is not null)
        {
            ValidateP2trPkScript(claim.ReceiverPkScript, "nonInteractiveClaim.ReceiverPkScript");
        }
        if (refund is not null)
        {
            ValidateP2trPkScript(refund.SenderPkScript, "nonInteractiveRefund.SenderPkScript");
        }

        // The non-interactive leaves are the only ones carrying a covenant — the signature leaves
        // assert nothing about value — so they are the only place an asset can be bound. Accepting
        // one without either would emit a sat-only contract and say nothing about it: the caller
        // funds it believing the asset is protected, and any spend satisfying the sat covenant walks
        // off with the asset.
        if (asset is not null)
        {
            if (claim is null && refund is null)
            {
                throw new ArgumentException(
                    "asset has no effect without nonInteractiveClaim or nonInteractiveRefund", nameof(asset));
            }
            ValidateAsset(asset, nameof(asset));
        }

        if (claim?.Strict is not { } strict)
        {
            return;
        }

        // `out >= 0` is satisfied by every output, so a zero would compile a bound that reads like
        // enforcement and enforces nothing.
        if (strict.Amount <= 0)
        {
            throw new ArgumentException(
                $"strict claim amount must be positive, got {strict.Amount}", nameof(claim));
        }
        // Strict on the sat CARRIER while the asset — the actual amount — goes unbounded. The most
        // dangerous shape here, because the caller has explicitly asked for enforcement and would
        // get it on the wrong quantity.
        if (asset is not null && strict.AssetAmount is null)
        {
            throw new ArgumentException(
                "strict claim needs an asset amount when the contract is denominated in an asset: " +
                "bounding only the sats would leave the asset amount unenforced", nameof(claim));
        }
        if (asset is null && strict.AssetAmount is not null)
        {
            throw new ArgumentException(
                "strict claim asset amount has no effect without an asset", nameof(claim));
        }
        if (strict.AssetAmount is { } assetAmount && assetAmount <= 0)
        {
            throw new ArgumentException(
                $"strict claim asset amount must be positive, got {assetAmount}", nameof(claim));
        }
    }

    private static void ValidateAsset(VHTLCv2Asset asset, string name)
    {
        if (asset.GenesisTxid.Length != 32)
        {
            throw new ArgumentException(
                $"{name} genesis txid must be 32 bytes, got {asset.GenesisTxid.Length}", name);
        }
        if (asset.GroupIndex is < 0 or > MaxAssetGroupIndex)
        {
            throw new ArgumentException(
                $"{name} group index must be in [0, {MaxAssetGroupIndex}], got {asset.GroupIndex}", name);
        }
    }

    private static void ValidateP2trPkScript(byte[] pkScript, string name)
    {
        if (pkScript.Length != 34 || pkScript[0] != 0x51 || pkScript[1] != 0x20)
        {
            throw new ArgumentException(
                $"{name} must be a P2TR scriptPubKey (0x5120 followed by 32 bytes)", name);
        }
    }

    private static void RequireBothOrNeither(string? first, string? second, string firstKey, string secondKey)
    {
        if ((first is null) != (second is null))
        {
            throw new FormatException($"'{firstKey}' and '{secondKey}' must both be present or both absent");
        }
    }
}
