using System.Globalization;
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

            // A send fails for two reasons that are both recoverable, and telling them apart
            // costs a round trip we would spend on the fix anyway. Either the wallet's VTXOs
            // have passed their renewal deadline (settle re-anchors them into a fresh
            // spendable set), or it has actually been drained (boarding adds new value, and
            // the settle that follows absorbs it). So try the cheap fix, then the thorough one.
            var settle = await TrySettle(ct);

            var second = await TrySend(arkadeAddress, amountSats, ct);
            if (second.IsSuccess) return TxidOf(second.StandardOutput);

            // Boarding buries 6 blocks to confirm the UTXO, and block height is not ours to
            // move on a whim — the timelock suites read it. So only board once the wallet is
            // demonstrably short. The balance over-reports (it counts recoverable VTXOs), and
            // the settle above has just renewed those, so falling below the ask here means
            // genuinely out of money rather than out of *spendable* money. A send that keeps
            // failing on a wallet that can afford it is not a liquidity problem, and mining
            // at it would only corrupt the chain state for whatever runs next.
            var balance = await OffchainBalance(ct);
            string board;
            if (balance >= 0 && balance >= amountSats)
            {
                board = $"skipped — wallet reports {balance} sats, enough to cover {amountSats}";
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
                $"  send:     {Describe(first)}\n" +
                $"  settle:   {Describe(settle)}\n" +
                $"  retry:    {Describe(second)}\n" +
                $"  boarding: {board}\n" +
                $"  balance:  {await ReadBalance(ct)}\n" +
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
    /// The CLI wallet's offchain balance in sats, as arkd's client library reports it.
    /// <para>
    /// Diagnostics only — never gate a send on this. The figure counts recoverable (swept
    /// but unspent) VTXOs, which a plain offchain send cannot touch, so it can read healthy
    /// on a wallet that cannot spend a satoshi. The send itself is the only honest probe.
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SettleTimeout);
        return await ArkCli(["settle", "--password", Password], timeout.Token);
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
