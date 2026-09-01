using System.Text.Json;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Arkade.Crypto;
using NArk.Arkade.Scripts;
using NArk.Core.Enums;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Contracts;

/// <summary>
/// The VHTLC used by the Lightning swap corridors: the six leaves of the reference VHTLC
/// construction, plus — when <see cref="EmulatorCovenants"/> is set — the covenant suite whose
/// co-signer is an emulator key tweaked by a covenant that pins where the spend may pay. The
/// suite is all or nothing, so this class is six leaves without the group, the full nine with
/// it, and eight when the group's legacy selector rebuilds the pre-timelocked-refund shape.
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

    /// <summary>The party that funds the contract, and that the refund paths pay back.</summary>
    public OutputDescriptor Sender { get; }

    /// <summary>The party that claims by revealing the preimage.</summary>
    public OutputDescriptor Receiver { get; }

    /// <summary>HASH160 of the preimage.</summary>
    public uint160 Hash { get; }

    /// <summary>When the sender's timelocked refund path opens.</summary>
    public LockTime RefundLocktime { get; }

    /// <summary>How long after funding the receiver may claim without the server.</summary>
    public Sequence UnilateralClaimDelay { get; }

    /// <summary>How long after funding sender and receiver may refund without the server.</summary>
    public Sequence UnilateralRefundDelay { get; }

    /// <summary>How long after funding the sender alone may refund, needing nobody.</summary>
    public Sequence UnilateralRefundWithoutReceiverDelay { get; }

    /// <summary>
    /// The emulator covenant suite, or null for the plain six-leaf VHTLC. All or nothing: one
    /// group carries every covenant leaf's parameters, so the tree can never name one leaf
    /// without the others or two different emulator keys.
    /// </summary>
    public EmulatorCovenants? EmulatorCovenants { get; }

    public VHTLCv2Contract(
        OutputDescriptor server,
        OutputDescriptor sender,
        OutputDescriptor receiver,
        uint160 hash,
        LockTime refundLocktime,
        Sequence unilateralClaimDelay,
        Sequence unilateralRefundDelay,
        Sequence unilateralRefundWithoutReceiverDelay,
        EmulatorCovenants? emulatorCovenants = null) : base(server)
    {
        ValidateTimeLock(unilateralClaimDelay, nameof(unilateralClaimDelay));
        ValidateTimeLock(unilateralRefundDelay, nameof(unilateralRefundDelay));
        ValidateTimeLock(unilateralRefundWithoutReceiverDelay, nameof(unilateralRefundWithoutReceiverDelay));

        Sender = sender;
        Receiver = receiver;
        Hash = hash;
        RefundLocktime = refundLocktime;
        UnilateralClaimDelay = unilateralClaimDelay;
        UnilateralRefundDelay = unilateralRefundDelay;
        UnilateralRefundWithoutReceiverDelay = unilateralRefundWithoutReceiverDelay;
        EmulatorCovenants = emulatorCovenants;
        // Computed once here, not re-derived per leaf build, so CreateNonInteractiveRefundScript()
        // and CreateNonInteractiveRefundWithoutReceiverScript() are structurally guaranteed to pin
        // the same destination rather than merely happening to match.
        _nonInteractiveRefundCosigner = emulatorCovenants is { } covenants
            ? CovenantKey(covenants.EmulatorPubKey, covenants.SenderPkScript)
            : null;
    }

    /// <inheritdoc />
    public override string Type => ContractType;

    /// <summary>The discriminator this contract serializes under.</summary>
    public const string ContractType = "HTLCv2";

    /// <inheritdoc />
    public override ContractScope DefaultScope => ContractScope.Offchain;

    /// <summary>
    /// The six signature leaves, in the order that fixes the merkle root, followed by the
    /// covenant suite when <see cref="EmulatorCovenants"/> is set: claim, refund, and — unless
    /// its legacy selector says otherwise — the timelocked refund last. Do not reorder — every
    /// earlier leaf's index is load-bearing for existing addresses.
    /// </summary>
    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        yield return CreateClaimScript();
        yield return CreateRefundScript();
        yield return CreateRefundWithoutReceiverScript();
        yield return CreateUnilateralClaimScript();
        yield return CreateUnilateralRefundScript();
        yield return CreateUnilateralRefundWithoutReceiverScript();
        if (EmulatorCovenants is null)
            yield break;
        yield return CreateNonInteractiveClaimScript();
        yield return CreateNonInteractiveRefundScript();
        if (EmulatorCovenants.Legacy is null)
            yield return CreateNonInteractiveRefundWithoutReceiverScript();
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
    /// <exception cref="InvalidOperationException">
    /// <see cref="EmulatorCovenants"/> is not set, so this leaf is not part of the taproot tree —
    /// a witness built against it would be rejected on-chain with no SDK-level error otherwise.
    /// </exception>
    public ScriptBuilder CreateNonInteractiveClaimScript()
    {
        if (EmulatorCovenants is not { } covenants)
        {
            throw new InvalidOperationException("VHTLC has no emulator covenant leaves");
        }

        return new CollaborativePathArkTapScript(
            CovenantKey(covenants.EmulatorPubKey, covenants.ReceiverPkScript),
            new CompositeTapScript(PreimageCondition(), new VerifyTapScript(),
                new NofNMultisigTapScript([ServerKey])));
    }

    /// <summary>
    /// Server + receiver + the covenant key, pinned to the sender's own payout. Releases the refund
    /// the moment those two agree the swap failed, without waiting out <see cref="RefundLocktime"/>
    /// and without a sender signature.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="EmulatorCovenants"/> is not set, so this leaf is not part of the taproot tree.
    /// </exception>
    public ScriptBuilder CreateNonInteractiveRefundScript() =>
        new CollaborativePathArkTapScript(
            RefundCosigner, new NofNMultisigTapScript([ServerKey, ReceiverKey]));

    /// <summary>
    /// Server + the covenant key, pinned to the sender's own payout, spendable after
    /// <see cref="RefundLocktime"/> with no receiver signature at all — the non-interactive
    /// analogue of <see cref="CreateRefundWithoutReceiverScript"/>. Reuses
    /// <see cref="_nonInteractiveRefundCosigner"/> rather than re-deriving it, so this leaf and
    /// <see cref="CreateNonInteractiveRefundScript"/> commit to the same key by construction.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The contract has no covenant suite, or its <see cref="EmulatorCovenants.Legacy"/> selector
    /// names the pre-timelocked-refund shape — either way this leaf is not part of the taproot
    /// tree, and a witness built against it would be rejected on-chain with no SDK-level error
    /// otherwise.
    /// </exception>
    public ScriptBuilder CreateNonInteractiveRefundWithoutReceiverScript()
    {
        if (EmulatorCovenants is null)
        {
            throw new InvalidOperationException("VHTLC has no emulator covenant leaves");
        }
        if (EmulatorCovenants.Legacy is not null)
        {
            throw new InvalidOperationException("VHTLC has no non-interactive refund-without-receiver leaf");
        }

        return new CollaborativePathArkTapScript(
            RefundCosigner,
            new CompositeTapScript(new LockTimeTapScript(RefundLocktime), new NofNMultisigTapScript([ServerKey])));
    }

    /// <summary>
    /// The covenant-tweaked emulator key that both <c>nonInteractiveRefund</c> leaves co-sign with.
    /// Computed once in the constructor — not re-derived per leaf build — so the two leaves are
    /// structurally guaranteed to pin the same destination rather than merely happening to match.
    /// Null exactly when <see cref="EmulatorCovenants"/> is.
    /// </summary>
    private readonly ECXOnlyPubKey? _nonInteractiveRefundCosigner;

    private ECXOnlyPubKey RefundCosigner =>
        _nonInteractiveRefundCosigner
        ?? throw new InvalidOperationException("VHTLC has no emulator covenant leaves");

    /// <summary>
    /// The ArkadeScript a covenant leaf's key commits to: "the output at this input's index pays
    /// <paramref name="destinationPkScript"/>, for at least what the input was worth".
    /// </summary>
    public static byte[] EnforcePayTo(byte[] destinationPkScript)
    {
        ValidateP2trPkScript(destinationPkScript, nameof(destinationPkScript));
        // The covenant commits to the 32-byte key alone; the 0x5120 P2TR prefix is re-added by the
        // introspection opcode that reads the output script back.
        var key = Convert.ToHexString(destinationPkScript[2..]).ToLowerInvariant();
        return ArkadeScript.AsmToBytes(
            "PUSHCURRENTINPUTINDEX DUP INSPECTOUTPUTSCRIPTPUBKEY OP_1 EQUALVERIFY " +
            $"{key} EQUALVERIFY INSPECTOUTPUTVALUE PUSHCURRENTINPUTINDEX INSPECTINPUTVALUE GREATERTHANOREQUAL");
    }

    /// <summary>
    /// The emulator key tweaked by a covenant, which is what makes the emulator's signature
    /// conditional on the spend actually honouring that covenant.
    /// </summary>
    private static ECXOnlyPubKey CovenantKey(ECXOnlyPubKey emulatorPubKey, byte[] destinationPkScript) =>
        ECXOnlyPubKey.Create(
            ArkadeTweak.Tweak(new TaprootPubKey(emulatorPubKey.ToBytes()), EnforcePayTo(destinationPkScript))
                .ToBytes());

    /// <summary>The sender's x-only key, as the leaves commit to it.</summary>
    public ECXOnlyPubKey SenderKey => Sender.ToXOnlyPubKey();

    /// <summary>The receiver's x-only key, as the leaves commit to it.</summary>
    public ECXOnlyPubKey ReceiverKey => Receiver.ToXOnlyPubKey();

    private ECXOnlyPubKey ServerKey =>
        Server?.ToXOnlyPubKey() ?? throw new InvalidOperationException("Server key is required");

    private HashLockTapScript PreimageCondition() =>
        new(Hash.ToBytes(false), HashLockTypeOption.Hash160, PreimageSize);

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
        if (EmulatorCovenants is { } covenants)
        {
            data["emulator"] = Convert.ToHexString(covenants.EmulatorPubKey.ToBytes()).ToLowerInvariant();
            data["niClaimPkScript"] = Convert.ToHexString(covenants.ReceiverPkScript).ToLowerInvariant();
            data["niRefundPkScript"] = Convert.ToHexString(covenants.SenderPkScript).ToLowerInvariant();
            // This key's name and its "1" value match ts-sdk's VHTLCV2ContractHandler.serializeParams
            // exactly and deliberately, so the FLAG ITSELF round-trips identically between the two
            // SDKs. That does not generalize to the rest of this dictionary: the two SDKs' param maps
            // are not interchangeable. The "type" discriminator differs ("HTLCv2" vs "vhtlc-v2"), so
            // does every delay key name (unilateralClaimDelay vs claimDelay, and so on), and the
            // covenant pkScript keys (niClaimPkScript vs nonInteractiveClaimReceiverPkScript, and so
            // on); one shared "emulator" key here stands in for two there
            // (nonInteractiveClaimEmulatorPubkey / nonInteractiveRefundEmulatorPubkey, both holding
            // the suite's one key); and sender/receiver/server serialize as output descriptors here
            // versus raw hex pubkeys there. The flag is the legacy marker by ABSENCE: a row without
            // it re-derives the pre-timelocked-refund eight-leaf script — the same encoding older
            // SDKs wrote for eight-leaf rows, so those rows read back unchanged.
            if (covenants.Legacy is null)
            {
                data["nonInteractiveRefundWithoutReceiver"] = "1";
            }
        }
        return data;
    }

    /// <summary>Rebuild a contract from the data <see cref="GetContractData"/> wrote.</summary>
    /// <param name="contractData">The parsed <c>arkcontract=</c> fields.</param>
    /// <param name="network">The network the descriptors belong to.</param>
    /// <returns>The reconstructed contract.</returns>
    public static ArkContract Parse(Dictionary<string, string> contractData, Network network) =>
        new VHTLCv2Contract(
            KeyExtensions.ParseOutputDescriptor(contractData["server"], network),
            KeyExtensions.ParseOutputDescriptor(contractData["sender"], network),
            KeyExtensions.ParseOutputDescriptor(contractData["receiver"], network),
            new uint160(contractData["hash"]),
            new LockTime(uint.Parse(contractData["refundLocktime"])),
            new Sequence(uint.Parse(contractData["unilateralClaimDelay"])),
            new Sequence(uint.Parse(contractData["unilateralRefundDelay"])),
            new Sequence(uint.Parse(contractData["unilateralRefundWithoutReceiverDelay"])),
            ParseEmulatorCovenants(contractData));

    /// <summary>
    /// Reads the covenant suite <see cref="GetContractData"/> wrote. The three keys are
    /// all-or-nothing: a row carrying some but not all was written for a leaf subset this class
    /// no longer derives, and rebuilding it would silently drop a leaf — a different address.
    /// Absent entirely means the plain six-leaf contract. The legacy marker is the ABSENCE of
    /// the <c>nonInteractiveRefundWithoutReceiver</c> flag on a covenant-bearing row — the same
    /// encoding older SDKs wrote for eight-leaf rows; present, it must be exactly <c>"1"</c>:
    /// silently reading any other value as absent would re-derive the eight-leaf script instead
    /// of surfacing that the stored row is malformed. Matches ts-sdk's
    /// <c>VHTLCV2ContractHandler.deserializeParams</c>, so the two SDKs fail identically on bad
    /// input rather than quietly disagreeing about what a contract's address is.
    /// </summary>
    private static EmulatorCovenants? ParseEmulatorCovenants(Dictionary<string, string> contractData)
    {
        var emulator = contractData.GetValueOrDefault("emulator");
        var niClaim = contractData.GetValueOrDefault("niClaimPkScript");
        var niRefund = contractData.GetValueOrDefault("niRefundPkScript");
        var present = (emulator is not null ? 1 : 0) + (niClaim is not null ? 1 : 0) +
            (niRefund is not null ? 1 : 0);
        if (present == 0)
        {
            if (contractData.ContainsKey("nonInteractiveRefundWithoutReceiver"))
            {
                throw new ArgumentException(
                    "nonInteractiveRefundWithoutReceiver without the emulator covenant keys it " +
                    "extends: reading this as 'not set' would re-derive a script without the leaf");
            }
            return null;
        }
        if (present != 3)
        {
            throw new ArgumentException(
                "emulator covenant params are all-or-nothing: 'emulator', 'niClaimPkScript' and " +
                "'niRefundPkScript' must all be present or all absent — a row carrying some was " +
                "written for a leaf subset this contract no longer derives");
        }

        var flag = contractData.GetValueOrDefault("nonInteractiveRefundWithoutReceiver");
        if (flag is not null && flag != "1")
        {
            throw new ArgumentException(
                "nonInteractiveRefundWithoutReceiver must be \"1\" when present, got "
                + JsonSerializer.Serialize(flag));
        }

        return new EmulatorCovenants(
            ECXOnlyPubKey.Create(Convert.FromHexString(emulator!)),
            Convert.FromHexString(niClaim!),
            Convert.FromHexString(niRefund!),
            flag is null ? EmulatorCovenantsLegacy.PreTimelockedRefund : null);
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
        if (preimage.Length != PreimageSize)
        {
            throw new ArgumentException(
                $"preimage must be {PreimageSize} bytes, got {preimage.Length}", nameof(preimage));
        }

        var digest = NBitcoin.Crypto.Hashes.RIPEMD160(
            NBitcoin.Crypto.Hashes.SHA256(preimage), 32);
        if (!digest.SequenceEqual(Hash.ToBytes(false)))
        {
            throw new ArgumentException(
                "this preimage does not hash to the contract's committed digest", nameof(preimage));
        }

        return new ArkCoin(
            walletIdentifier, this, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut, Receiver,
            CreateClaimScript(), new WitScript(Op.GetPushOp(preimage)), null, null,
            vtxo.Swept, vtxo.Unrolled, assets: vtxo.Assets);
    }

    /// <summary>
    /// The sender's own timelocked way out: <c>refundWithoutReceiver</c>, co-signed by the Arkade
    /// server once <see cref="RefundLocktime"/> has passed.
    /// </summary>
    /// <param name="walletIdentifier">The wallet holding the sender's key.</param>
    /// <param name="vtxo">The unspent lockup output.</param>
    /// <returns>A coin spendable through that leaf.</returns>
    /// <remarks>
    /// This is the sender's recourse that needs no counterparty. The neighbouring
    /// <c>nonInteractiveRefund</c> leaf resolves faster and without waiting out the locktime, but it
    /// is not an alternative here: it needs the <em>receiver's</em> signature, so it exists for the
    /// two of them to release a failed swap by agreement, not for the sender to exit alone. Unlike
    /// that leaf this one carries no covenant, so the payout is not pinned — the caller chooses
    /// where it goes.
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

    private static void ValidateTimeLock(Sequence sequence, string name)
    {
        if (!sequence.IsRelativeLock)
        {
            throw new ArgumentException($"{name} must be a relative timelock", name);
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
}
