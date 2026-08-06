using NArk.Arkade.Contracts;
using NArk.Arkade.Program.Models;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Lightning;

/// <summary>
/// Constructor arguments for the Lightning send leg's covenant swap script — everything the maker
/// needs to derive the contract it funds. All of it is the maker's own data except
/// <see cref="Receiver"/> and <see cref="RefundLocktime"/>, which are binding fields of the solver's
/// RFQ quote.
/// </summary>
/// <param name="Receiver">The solver's x-only settlement key (<c>solver_pubkey</c> from the quote).</param>
/// <param name="PreimageHash">
/// The 20-byte HASH160 the script commits to. A BOLT11 payment hash is <c>sha256(P)</c> while the
/// script's branch commits to <c>ripemd160(sha256(P))</c>; see <see cref="PreimageHashFromPaymentHash"/>.
/// </param>
/// <param name="RefundLocktime">
/// Absolute BIP65 deadline in unix seconds (<c>refund_locktime</c> from the quote), after which the
/// covenant refund path opens.
/// </param>
/// <param name="ClaimDelay">
/// Relative BIP68 delay in seconds for the solver's server-independent claim. Must be a whole
/// multiple of 512 s; derive it from the Arkade server's own unilateral exit delay rather than
/// hardcoding one — see <see cref="CeilToGranularity"/>.
/// </param>
/// <param name="EmulatorPubkey">The emulator signer key (32-byte x-only or 33-byte compressed).</param>
/// <param name="RefundPkScript">
/// Where a refund must pay: the maker's own P2TR scriptPubKey (<c>0x5120</c> + 32 bytes).
/// </param>
public sealed record CovenantSwapParams(
    byte[] Receiver,
    byte[] PreimageHash,
    uint RefundLocktime,
    uint ClaimDelay,
    byte[] EmulatorPubkey,
    byte[] RefundPkScript);

/// <summary>
/// The three-leaf covenant swap script for the <c>arkade:BTC-&gt;lightning:BTC</c> corridor — the
/// contract a maker derives locally and funds to accept a solver's quote.
/// </summary>
/// <remarks>
/// <para>
/// Pinned to the solver side's output by the golden vectors in
/// <c>NArk.Tests/ArkadeIntents/Fixtures/covenant_swap.json</c>. The leaves are:
/// </para>
/// <list type="bullet">
/// <item><c>claim</c> — preimage + solver + Arkade server. The solver claims by revealing the
/// preimage that paying the invoice yields.</item>
/// <item><c>refund</c> — after <see cref="CovenantSwapParams.RefundLocktime"/>: the Arkade server
/// plus a covenant key. The ArkadeScript segment pins the spend's output to the maker's own script
/// with value ≥ input, so <em>anyone</em> may push the refund and it can only pay one place. The
/// maker holds no key on this path, signs nothing, and keeps no state.</item>
/// <item><c>unilateralClaim</c> — preimage + solver alone after a CSV delay: the solver's recourse
/// if the Arkade server disappears between paying the invoice and claiming.</item>
/// </list>
/// <para>
/// Unlike the offer programs in <c>Programs/</c>, this program is built in code rather than parsed
/// from an embedded artifact: its timelocks are per-swap values, and the artifact parser resolves
/// <c>cltv</c>/<c>csv</c> to concrete <see cref="LockTime"/>/<see cref="Sequence"/> at parse time —
/// it does not accept <c>$param</c> placeholders there.
/// </para>
/// </remarks>
public static class CovenantSwapProgram
{
    /// <summary>BIP65: at or above this value a locktime is a unix timestamp rather than a block height.</summary>
    public const uint LocktimeThreshold = 500_000_000;

    /// <summary>BIP68 encodes relative time in units of 512 seconds.</summary>
    public const uint SequenceGranularitySeconds = 512;

    /// <summary>Round a duration up to the next whole BIP68 512-second unit.</summary>
    /// <param name="seconds">The duration to round, in seconds.</param>
    /// <returns>The smallest multiple of 512 that is greater than or equal to <paramref name="seconds"/>.</returns>
    public static uint CeilToGranularity(uint seconds) =>
        (seconds + SequenceGranularitySeconds - 1) / SequenceGranularitySeconds * SequenceGranularitySeconds;

    /// <summary>
    /// Bridge a BOLT11 payment hash to the 20-byte hash the script commits to: the invoice carries
    /// <c>sha256(P)</c>, the script's HASH160 branch commits to <c>ripemd160(sha256(P))</c>. This is
    /// why the maker never needs to see the preimage — paying the invoice is what yields it.
    /// </summary>
    /// <param name="paymentHash">The invoice's 32-byte payment hash.</param>
    /// <returns>The 20-byte HASH160 the covenant script commits to.</returns>
    /// <exception cref="ArgumentException">The payment hash is not 32 bytes.</exception>
    public static byte[] PreimageHashFromPaymentHash(byte[] paymentHash)
    {
        if (paymentHash.Length != 32)
        {
            throw new ArgumentException(
                $"payment hash must be 32 bytes, got {paymentHash.Length}", nameof(paymentHash));
        }
        return NBitcoin.Crypto.Hashes.RIPEMD160(paymentHash, paymentHash.Length);
    }

    /// <summary>
    /// Build the swap program with this swap's timelocks compiled in. The pubkey and hash arguments
    /// stay as <c>$param</c> placeholders, bound at contract construction.
    /// </summary>
    /// <param name="parameters">The per-swap constructor arguments.</param>
    /// <returns>The program artifact, ready to compile.</returns>
    /// <exception cref="ArgumentException">A parameter is malformed — see the validation notes on each field.</exception>
    public static ArkadeProgram Build(CovenantSwapParams parameters)
    {
        Validate(parameters);

        // Declared so the validator binds and type-checks them; $server auto-binds from the
        // contract's own server descriptor, exactly as the offer programs rely on.
        var declaredParams = new TypedInput[]
        {
            new() { Name = "receiver", Type = InputType.Pubkey },
            new() { Name = "server", Type = InputType.Pubkey },
            new() { Name = "preimageHash", Type = InputType.Hash },
            new() { Name = "refundKey", Type = InputType.Pubkey },
        };

        // Function order is the taproot tree's leaf order — keep it identical to the reference
        // implementation's artifact or the derived address diverges.
        var functions = new Dictionary<string, ArkadeFunction>
        {
            ["claim"] = new()
            {
                Tapscript = new TapscriptSegment
                {
                    Signers = [AsmToken.FromText("$receiver"), AsmToken.FromText("$server")],
                    Asm = PreimageConditionAsm(AsmToken.FromText("$preimageHash")),
                },
            },
            ["refund"] = new()
            {
                Tapscript = new TapscriptSegment
                {
                    Signers = [AsmToken.FromText("$server")],
                    Cltv = new LockTime(parameters.RefundLocktime),
                },
                ScriptSegment = new ArkadeScriptSegment
                {
                    Asm = EnforcePayToAsm(AsmToken.FromText("$refundKey")),
                },
            },
            ["unilateralClaim"] = new()
            {
                Tapscript = new TapscriptSegment
                {
                    Signers = [AsmToken.FromText("$receiver")],
                    Asm = PreimageConditionAsm(AsmToken.FromText("$preimageHash")),
                    Csv = new Sequence(TimeSpan.FromSeconds(parameters.ClaimDelay)),
                },
            },
        };

        return new ArkadeProgram
        {
            Version = ArkadeProgram.SupportedVersion,
            Name = "covenant-swap",
            Params = declaredParams,
            Functions = functions,
        };
    }

    /// <summary>
    /// Compile the swap into a spendable contract. The compiler appends the covenant-tweaked
    /// co-signer key to the <c>refund</c> leaf, so only the emulator can complete a refund and only
    /// after it has verified the covenant.
    /// </summary>
    /// <param name="parameters">The per-swap constructor arguments.</param>
    /// <param name="server">Output descriptor for the Arkade server's signer key, bound as <c>$server</c>.</param>
    /// <returns>The compiled contract; <c>GetArkAddress()</c> is the address to fund.</returns>
    public static ArkProgramContract BuildContract(CovenantSwapParams parameters, OutputDescriptor server)
    {
        var program = Build(parameters);

        var args = new Dictionary<string, AsmToken>
        {
            ["receiver"] = AsmToken.FromBytes(parameters.Receiver),
            ["preimageHash"] = AsmToken.FromBytes(parameters.PreimageHash),
            // The covenant commits to the x-only key alone; the 0x5120 P2TR prefix is re-added by
            // the introspection opcode that reads the output script.
            ["refundKey"] = AsmToken.FromBytes(parameters.RefundPkScript[2..]),
        };

        var emulator = ECXOnlyPubKey.Create(NormalizeToXOnly(parameters.EmulatorPubkey));

        // No $user: this contract has no maker-keyed path. Its refund needs no maker signature at all.
        return new ArkProgramContract(server, program, args, user: null, emulatorKey: emulator);
    }

    /// <summary><c>HASH160 &lt;hash20&gt; EQUAL</c> — the preimage condition both claim leaves share.</summary>
    private static AsmToken[] PreimageConditionAsm(AsmToken hash) =>
        [AsmToken.FromText("HASH160"), hash, AsmToken.FromText("EQUAL")];

    /// <summary>
    /// The covenant: "this input's output pays the given P2TR program, value ≥ input".
    /// </summary>
    private static AsmToken[] EnforcePayToAsm(AsmToken refundKey) =>
    [
        AsmToken.FromText("PUSHCURRENTINPUTINDEX"),
        AsmToken.FromText("DUP"),
        AsmToken.FromText("INSPECTOUTPUTSCRIPTPUBKEY"),
        AsmToken.FromNumber(1),
        AsmToken.FromText("EQUALVERIFY"),
        refundKey,
        AsmToken.FromText("EQUALVERIFY"),
        AsmToken.FromText("INSPECTOUTPUTVALUE"),
        AsmToken.FromText("PUSHCURRENTINPUTINDEX"),
        AsmToken.FromText("INSPECTINPUTVALUE"),
        AsmToken.FromText("GREATERTHANOREQUAL"),
    ];

    private static void Validate(CovenantSwapParams parameters)
    {
        if (parameters.Receiver.Length != 32)
        {
            throw new ArgumentException(
                $"receiver must be a 32-byte x-only key, got {parameters.Receiver.Length}", nameof(parameters));
        }
        if (parameters.PreimageHash.Length != 20)
        {
            throw new ArgumentException(
                $"preimage hash must be 20 bytes (HASH160), got {parameters.PreimageHash.Length}", nameof(parameters));
        }
        // Below the threshold a verifier reads the locktime as a block height, and block-interval
        // variance is far too wide to hold a Lightning HTLC deadline against.
        if (parameters.RefundLocktime < LocktimeThreshold)
        {
            throw new ArgumentException(
                $"refundLocktime {parameters.RefundLocktime} is below LOCKTIME_THRESHOLD ({LocktimeThreshold}) " +
                "and would be interpreted as a block height", nameof(parameters));
        }
        // A delay that is not a whole number of 512s units cannot be encoded, and rounding it here
        // would silently derive a script other than the one the solver expects.
        if (parameters.ClaimDelay == 0 || parameters.ClaimDelay % SequenceGranularitySeconds != 0)
        {
            throw new ArgumentException(
                $"claimDelay must be a positive multiple of {SequenceGranularitySeconds}s, got {parameters.ClaimDelay}",
                nameof(parameters));
        }
        if (parameters.EmulatorPubkey.Length is not (32 or 33))
        {
            throw new ArgumentException(
                $"emulator pubkey must be 32 or 33 bytes, got {parameters.EmulatorPubkey.Length}", nameof(parameters));
        }
        if (parameters.RefundPkScript.Length != 34
            || parameters.RefundPkScript[0] != 0x51
            || parameters.RefundPkScript[1] != 0x20)
        {
            throw new ArgumentException(
                "refund destination must be a P2TR scriptPubKey (0x5120 + 32 bytes)", nameof(parameters));
        }
    }

    private static byte[] NormalizeToXOnly(byte[] pubkey) => pubkey.Length == 33 ? pubkey[1..] : pubkey;
}
