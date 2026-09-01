using NBitcoin.Secp256k1;

namespace NArk.Arkade.Contracts;

/// <summary>
/// Which historical shape of the covenant suite to rebuild. Never for a new lockup.
/// </summary>
public enum EmulatorCovenantsLegacy
{
    /// <summary>
    /// The suite WITHOUT its timelocked refund leaf: the shape every emulator-covenant lockup
    /// funded before that leaf shipped carries. Lockups already funded in that shape keep it
    /// permanently — a leaf cannot be retrofitted onto an address already committed — so
    /// re-deriving such a lockup (to spend it, or to verify an old quote's address) needs this.
    /// A new lockup that omits the leaf gives up the one refund tier needing nobody, for nothing.
    /// </summary>
    PreTimelockedRefund,
}

/// <summary>
/// The emulator covenant suite, all or nothing. Present on a <see cref="VHTLCv2Contract"/>, the
/// three leaves whose co-signer is the emulator key tweaked by a covenant pinning where the spend
/// may pay — <c>nonInteractiveClaim</c>, <c>nonInteractiveRefund</c>, and, unless
/// <see cref="Legacy"/> selects an older shape, <c>nonInteractiveRefundWithoutReceiver</c> —
/// are appended to the six signature leaves. Absent, the contract is those six alone.
/// </summary>
/// <remarks>
/// One key, one decision: the per-leaf shape ts-sdk once offered admitted trees no deployment
/// would ever produce (claim without refund, or two different emulator keys) and no stored row
/// could faithfully describe, so both SDKs take the suite as a single group. The leaves share
/// <see cref="EmulatorPubKey"/> structurally — each tweaks it with the covenant for its own
/// destination — and BIP-341 tapscript sighashes committing to the tapleaf hash are what keep a
/// signature for one leaf from replaying against another.
/// </remarks>
public sealed class EmulatorCovenants
{
    /// <summary>The emulator service's key, which every leaf in the suite tweaks.</summary>
    public ECXOnlyPubKey EmulatorPubKey { get; }

    /// <summary>Where the claim covenant must pay — the receiver's P2TR scriptPubKey.</summary>
    public byte[] ReceiverPkScript { get; }

    /// <summary>
    /// Where BOTH refund covenants must pay — the sender's P2TR scriptPubKey. One destination
    /// shared by both refund leaves, so they cannot diverge on where a refund goes.
    /// </summary>
    public byte[] SenderPkScript { get; }

    /// <summary>Set only to rebuild a lockup funded before the current suite shape shipped.</summary>
    public EmulatorCovenantsLegacy? Legacy { get; }

    public EmulatorCovenants(
        ECXOnlyPubKey emulatorPubKey,
        byte[] receiverPkScript,
        byte[] senderPkScript,
        EmulatorCovenantsLegacy? legacy = null)
    {
        ValidateP2trPkScript(receiverPkScript, nameof(receiverPkScript));
        ValidateP2trPkScript(senderPkScript, nameof(senderPkScript));

        EmulatorPubKey = emulatorPubKey;
        ReceiverPkScript = receiverPkScript;
        SenderPkScript = senderPkScript;
        Legacy = legacy;
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
