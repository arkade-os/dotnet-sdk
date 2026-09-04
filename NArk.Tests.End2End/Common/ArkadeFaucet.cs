using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using NBitcoin;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Funds arbitrary Arkade addresses on the regtest stack. This is the single funding
/// entry point for the E2E suite — call it whenever a test needs spendable VTXOs at an
/// address it controls.
/// </summary>
/// <remarks>
/// <para>
/// The source of funds is the stack's own wallet, not a service of ours: arkade-regtest's
/// <c>setupArkd</c> initializes the <c>ark</c> client CLI inside the arkd container and
/// credits it with 1 BTC offchain from a server-issued note, while intent fees are still
/// zeroed. Every profile that starts arkd gets it, so any stack that can run an E2E test
/// can also fund one.
/// </para>
/// <para>
/// This replaced funding through Fulmine's <c>/api/v1/send/offchain</c>. Fulmine is still
/// part of the stack, but only in the role the delegation suite genuinely needs it for —
/// the <c>delegate</c> profile's delegator. Funding used to drag in the whole <c>boltz</c>
/// profile just to get a second Fulmine to draw from.
/// </para>
/// </remarks>
public static class ArkadeFaucet
{
    /// <summary>
    /// arkd's admin password, which is also what unlocks the CLI wallet living beside it.
    /// Matches arkade-regtest's <c>ARKD_PASSWORD</c> default; override it here if a stack
    /// is started with a different one.
    /// </summary>
    private static string Password =>
        Environment.GetEnvironmentVariable("ARKD_PASSWORD") ?? "secret";

    /// <summary>
    /// A settle or a boarding cycle mutates the one CLI wallet everything draws from, so
    /// two callers racing through the recovery ladder would double-board and double-settle.
    /// The E2E assembly is <c>NonParallelizable</c>, but the faucet does not rely on that.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Amount boarded when the CLI wallet has genuinely run dry. Large enough that a whole
    /// suite fits in one boarding cycle — each one costs ~6 blocks and a batch.
    /// </summary>
    private static readonly Money BoardingTopUp = Money.Coins(1);

    /// <summary>
    /// A settle joins the next batch, so it is bounded by the batch interval rather than
    /// by anything the CLI does locally.
    /// </summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The longest this will sit out an arkd ban before giving up and reporting it.
    /// </summary>
    /// <remarks>
    /// Bounded rather than open-ended, but sized from what arkd actually hands out rather than
    /// from a round number: the convictions that took a suite down ran to roughly four minutes
    /// past the failure (three of them, the last at +3m47s). A cap under that would have been a
    /// wait that always expires unused, which is worse than no wait at all — it costs the time
    /// and still fails.
    ///
    /// Beyond this the wallet is in a state waiting will not fix, and burning the suite's clock
    /// on it turns one legible failure into a timeout somewhere less obvious.
    /// </remarks>
    private static readonly TimeSpan MaxBanWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Sends <paramref name="amountSats"/> sats to <paramref name="arkadeAddress"/> as an
    /// offchain Arkade transaction, so the VTXO lands without waiting for a batch.
    /// </summary>
    /// <param name="arkadeAddress">Destination Arkade address, bech32m-encoded.</param>
    /// <param name="amountSats">
    /// Amount in satoshis. Sub-dust amounts are allowed — the protocol carries them as an
    /// OP_RETURN-scripted VTXO — which is what lets the sub-dust suite fund itself.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Arkade transaction id of the send.</returns>
    /// <exception cref="InvalidOperationException">
    /// The send failed and neither renewing nor re-boarding the CLI wallet fixed it.
    /// </exception>
    public static async Task<string> Fund(string arkadeAddress, long amountSats, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var first = await TrySend(arkadeAddress, amountSats, ct);
            if (first.IsSuccess) return TxidOf(first.StandardOutput);

            // Every rung below keys off SPENDABLE sats, never the balance. The distinction is
            // the whole reason this ladder exists: a wallet can hold plenty and spend none.
            var spendable = await SpendableSats(ct);

            // Settling is the expensive rung, and not because it is slow. It registers an
            // intent in a batch, and a batch that finalises with our forfeit unsigned gets the
            // wallet's script BANNED for minutes — after which nothing here works at all. So it
            // runs when the wallet genuinely cannot cover the ask, not as a reflex after any
            // failed send.
            //
            // A send failing while the spendable set covers the amount is not a liquidity
            // problem, and settling at it would buy nothing while taking on the ban risk.
            var settle = spendable >= 0 && spendable >= amountSats
                ? Skipped($"spendable {spendable} sats already covers {amountSats}")
                : Describe(await TrySettle(ct));

            var second = await TrySend(arkadeAddress, amountSats, ct);
            if (second.IsSuccess) return TxidOf(second.StandardOutput);

            // Boarding buries 6 blocks to confirm the UTXO, and block height is not ours to move
            // on a whim — the timelock suites read it. So it is the last rung, and it is gated on
            // the spendable figure re-read AFTER the settle above: that is the only number that
            // can say whether the wallet is out of money or merely out of spendable money.
            var spendableNow = await SpendableSats(ct);
            string board;
            if (spendableNow >= 0 && spendableNow >= amountSats)
            {
                board = $"skipped — {spendableNow} spendable sats already cover {amountSats}";
            }
            else
            {
                board = $"{await BoardTopUp(ct)}; re-settle: {Describe(await TrySettle(ct))}";

                var third = await TrySend(arkadeAddress, amountSats, ct);
                if (third.IsSuccess) return TxidOf(third.StandardOutput);
                board += $"; retry: {Describe(third)}";
            }

            throw new InvalidOperationException(
                $"Arkade faucet could not send {amountSats} sats to {arkadeAddress}.\n" +
                $"  send:      {Describe(first)}\n" +
                $"  spendable: {spendable} sats (before settle)\n" +
                $"  settle:    {settle}\n" +
                $"  retry:     {Describe(second)}\n" +
                $"  spendable: {spendableNow} sats (after settle)\n" +
                $"  boarding:  {board}\n" +
                $"  balance:   {await ReadBalance(ct)}\n" +
                "The stack's ark CLI wallet is seeded once at `regtest.mjs start`; if it cannot be " +
                "recovered, restart the stack (`node regtest/regtest.mjs clean && node regtest/regtest.mjs start`).");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Settles the CLI wallet: renews VTXOs that are past their renewal deadline and absorbs
    /// any confirmed boarding UTXO. Exposed for tests that want the wallet renewed up front
    /// rather than on a failed send.
    /// </summary>
    public static async Task Settle(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var result = await TrySettle(ct);
            if (!result.IsSuccess)
                throw new InvalidOperationException($"Arkade faucet settle failed: {Describe(result)}");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// What the CLI wallet can actually spend right now, in sats, or <c>-1</c> when unreadable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ark vtxos</c> lists the SPENDABLE set by default; <c>ark balance</c> does not — it
    /// counts recoverable and past-renewal VTXOs an offchain send cannot touch, so it reads
    /// healthy on a wallet that cannot spend a satoshi. Every decision here keys off this,
    /// never off the balance.
    /// </para>
    /// <para>
    /// The helper this replaced knew the same thing about Fulmine and said so: its balance
    /// counted VTXOs past their renewal deadline while a plain offchain send failed with
    /// "missing vtxos", so it gated on the spendable subset from <c>/api/v1/vtxos</c>. Reading
    /// the aggregate instead is what let a wallet reporting 93,600,000 sats fail every send for
    /// want of 500,000 — and, worse, convinced the ladder it was rich enough to skip the rung
    /// that would have fixed it.
    /// </para>
    /// </remarks>
    public static async Task<long> SpendableSats(CancellationToken ct = default)
    {
        var result = await ArkCli(["vtxos"], ct);
        if (!result.IsSuccess) return -1;
        try
        {
            var total = 0L;
            foreach (var vtxo in JsonDocument.Parse(result.StandardOutput).RootElement.EnumerateArray())
            {
                // Printed from a Go struct with no json tags, so the names come out PascalCase.
                // Matched case-insensitively anyway: a tag added upstream would otherwise turn
                // this into a silent zero, which reads exactly like an empty wallet.
                foreach (var field in vtxo.EnumerateObject())
                {
                    if (field.NameEquals("Amount") || field.Name.Equals("amount", StringComparison.OrdinalIgnoreCase))
                    {
                        total += field.Value.GetInt64();
                        break;
                    }
                }
            }
            return total;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// The CLI wallet's offchain balance in sats, as arkd's client library reports it.
    /// <para>
    /// Diagnostics only — never gate anything on this; see <see cref="SpendableSats"/> for why
    /// and for what to use instead. Kept because it is the number a human reads in a failure
    /// message, and the gap between it and the spendable figure is itself the diagnosis.
    /// </para>
    /// </summary>
    public static async Task<long> OffchainBalance(CancellationToken ct = default)
    {
        var result = await ArkCli(["balance"], ct);
        if (!result.IsSuccess) return -1;
        try
        {
            return JsonDocument.Parse(result.StandardOutput)
                .RootElement.GetProperty("offchain_balance").GetProperty("total").GetInt64();
        }
        catch
        {
            return -1;
        }
    }

    private static Task<BufferedCommandResult> TrySend(string arkadeAddress, long amountSats, CancellationToken ct)
        => ArkCli([
            "send",
            "--to", arkadeAddress,
            "--amount", amountSats.ToString(CultureInfo.InvariantCulture),
            "--password", Password
        ], ct);

    private static async Task<BufferedCommandResult> TrySettle(CancellationToken ct)
    {
        var first = await SettleOnce(ct);
        if (first.IsSuccess) return first;

        // A ban is the one settle failure that fixes itself. arkd bans a VtxoScript for a
        // period after a batch it joined went wrong — a forfeit signature that never arrived,
        // typically — and refuses to register any intent spending it until the period is up.
        // It also says exactly when, so there is nothing to guess: wait out the latest
        // conviction and try once more.
        //
        // Worth handling rather than surfacing, because the wallet is otherwise healthy and
        // every rung above and below this one depends on the settle. Left unhandled it took a
        // whole suite down: the wallet held 93,600,000 sats, none of them spendable until the
        // renewal that the ban was blocking.
        if (BannedUntil(first) is not { } until) return first;

        var wait = until - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        if (wait <= TimeSpan.Zero || wait > MaxBanWait) return first;

        await Task.Delay(wait, ct);
        return await SettleOnce(ct);
    }

    private static async Task<BufferedCommandResult> SettleOnce(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SettleTimeout);
        return await ArkCli(["settle", "--password", Password], timeout.Token);
    }

    /// <summary>
    /// When the wallet's ban lifts, or <c>null</c> when this failure is not a ban.
    /// </summary>
    /// <remarks>
    /// arkd reports one conviction per offence and each carries its own expiry; the wallet is
    /// usable again only after the LAST of them, so this takes the maximum rather than the
    /// first match. Parsed from the message because that is where arkd puts it — there is no
    /// structured field on the CLI's error path.
    /// </remarks>
    private static DateTimeOffset? BannedUntil(BufferedCommandResult result)
    {
        var text = result.StandardError + result.StandardOutput;
        if (!text.Contains("VTXO_BANNED", StringComparison.Ordinal)) return null;

        DateTimeOffset? latest = null;
        foreach (Match match in Regex.Matches(text, @"banned until (\S+?)(?:,|\s|$)"))
        {
            if (DateTimeOffset.TryParse(
                    match.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var parsed)
                && (latest is null || parsed > latest))
            {
                latest = parsed;
            }
        }
        return latest;
    }

    /// <summary>
    /// Sends on-chain BTC to the CLI wallet's boarding address and confirms it. The caller
    /// must settle afterwards — boarding only puts the value in front of the wallet, the
    /// settle is what turns it into VTXOs. Returns a description for the failure message.
    /// </summary>
    private static async Task<string> BoardTopUp(CancellationToken ct)
    {
        var receive = await ArkCli(["receive"], ct);
        if (!receive.IsSuccess)
            return $"could not read the boarding address: {Describe(receive)}";

        string boardingAddress;
        try
        {
            boardingAddress = JsonDocument.Parse(receive.StandardOutput)
                .RootElement.GetProperty("boarding_address").GetString()!;
        }
        catch (Exception ex)
        {
            return $"could not parse `ark receive` output ({ex.Message}): {receive.StandardOutput.Trim()}";
        }

        var txid = await DockerHelper.BitcoinSendToAddress(boardingAddress, BoardingTopUp, ct);

        // arkd will not take an unconfirmed boarding input, so bury it before settling.
        await DockerHelper.MineBlocks(6, ct);

        return $"boarded {BoardingTopUp} to {boardingAddress} (txid {txid})";
    }

    private static async Task<string> ReadBalance(CancellationToken ct)
    {
        var balance = await OffchainBalance(ct);
        return balance < 0 ? "unavailable" : $"{balance} sats offchain (includes unspendable recoverable VTXOs)";
    }

    /// <summary>
    /// Runs the <c>ark</c> client CLI inside the arkd container. The password is always
    /// passed explicitly: without it the CLI prompts on the terminal, and `docker exec`
    /// has no TTY here, so the command would hang instead of failing.
    /// </summary>
    private static async Task<BufferedCommandResult> ArkCli(string[] args, CancellationToken ct)
        => await Cli.Wrap("docker")
            .WithArguments(["exec", DockerHelper.Container.Arkd, "ark", .. args])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

    /// <summary>Renders a rung that was deliberately not run, so the ladder reads the same either way.</summary>
    private static string Skipped(string why) => $"skipped — {why}";

    private static string Describe(BufferedCommandResult result)
        => result.IsSuccess
            ? "ok"
            : $"exit={result.ExitCode} {result.StandardError.Trim()} {result.StandardOutput.Trim()}".Trim();

    /// <summary>
    /// <c>ark send</c> prints <c>{"txid": "..."}</c>. Falls back to the raw output rather
    /// than throwing: the send has already succeeded by this point, and no caller depends
    /// on the id beyond logging it.
    /// </summary>
    private static string TxidOf(string stdout)
    {
        try
        {
            return JsonDocument.Parse(stdout).RootElement.GetProperty("txid").GetString() ?? stdout.Trim();
        }
        catch
        {
            return stdout.Trim();
        }
    }
}
