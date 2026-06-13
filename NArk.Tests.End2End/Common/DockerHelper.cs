using System.Globalization;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWrap;
using CliWrap.Buffered;
using NBitcoin;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Utility for interacting with Docker containers from tests.
/// Replaces Aspire's ResourceCommands abstraction.
/// </summary>
public static class DockerHelper
{
    public static async Task StopContainer(string name, CancellationToken ct = default)
        => await Cli.Wrap("docker").WithArguments($"stop {name}")
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

    public static async Task StartContainer(string name, CancellationToken ct = default)
        => await Cli.Wrap("docker").WithArguments($"start {name}")
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

    public static async Task<string> Exec(string container, string[] args, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", container, .. args])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        return result.StandardOutput;
    }

    // denigiri's bitcoin container runs the btcpayserver image with
    // BITCOIN_NETWORK=regtest and rpcuser=admin1/rpcpassword=123, and keeps
    // exactly one wallet loaded so wallet RPCs route without an explicit
    // -rpcwallet. bitcoin-cli must carry these connection flags or it defaults
    // to mainnet (port 8332) and fails to connect.
    private static readonly string[] BitcoinCliArgs =
        ["bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123"];

    /// <summary>
    /// Runs bitcoin-cli inside the regtest bitcoin container with the correct
    /// connection flags. Returns trimmed stdout; throws on a non-zero exit.
    /// </summary>
    public static async Task<string> BitcoinCli(string[] args, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", Container.Bitcoin, .. BitcoinCliArgs, .. args])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"bitcoin-cli {string.Join(' ', args)} failed (exit={result.ExitCode}): {result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
    }

    public static async Task MineBlocks(int count = 20, CancellationToken ct = default)
        => await Exec(Container.Bitcoin, [.. BitcoinCliArgs, "-generate", count.ToString()], ct);

    /// <summary>
    /// Drives a Boltz submarine swap into a specific status via
    /// <c>boltzr-cli swap set-status</c>. Use <see cref="SubmarineSwapStatus"/> constants
    /// for the <paramref name="status"/> argument. For chain swaps use
    /// <see cref="TrySetBoltzSwapStatus"/> which falls back to a direct DB update.
    /// </summary>
    public static async Task SetBoltzSwapStatus(string swapId, string status, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments([
                "exec", Container.Boltz,
                "/boltz-backend/target/release/boltzr-cli",
                "-c", "/home/boltz/.boltz/certificates",
                "swap", "set-status", swapId, status
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"boltzr-cli swap set-status {swapId} {status} failed (exit={result.ExitCode}): " +
                $"stdout={result.StandardOutput.Trim()}, stderr={result.StandardError.Trim()}");
    }

    /// <summary>
    /// Creates an LND invoice on the nigiri lnd container.
    /// Returns the BOLT11 payment request string.
    /// </summary>
    public static async Task<string> CreateLndInvoice(long amtSats = 10000, int expirySecs = 30,
        CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "lncli", "--network=regtest", "addinvoice", "--amt", amtSats.ToString()
        };
        if (expirySecs > 0)
        {
            args.AddRange(["--expiry", expirySecs.ToString(CultureInfo.InvariantCulture)]);
        }

        var output = await Exec(Container.Lnd, args.ToArray(), ct);
        var invoice = JsonSerializer.Deserialize<JsonObject>(output)?["payment_request"]
                          ?.GetValue<string>()
                      ?? throw new InvalidOperationException($"Invoice creation on LND failed. Output: {output}");
        return invoice.Trim();
    }

    /// <summary>
    /// Not really a Docker helper, but a http fulmine endpoint one.
    /// Sends <paramref name="amountSats"/> sats as an Arkade VTXO to <paramref name="arkAddress"/>
    /// via Fulmine's offchain send API. Ensures Fulmine has sufficient balance before sending.
    /// </summary>
    public static async Task SendArkdNoteTo(string arkAddress, long amountSats,
        CancellationToken ct = default)
    {
        await FulmineLiquidityHelper.EnsureArkLiquidity(minBalance: amountSats, maxAttempts: 5);
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost:7003") };
        var response = await http.PostAsJsonAsync(
            "/api/v1/send/offchain",
            new { address = arkAddress, amount = amountSats },
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Fulmine send offchain to {arkAddress} for {amountSats} sats failed " +
                $"({response.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Creates an arkd note via docker exec.
    /// Returns the note string.
    /// </summary>
    public static async Task<string> CreateArkNote(long amountSats = 1000000, CancellationToken ct = default)
    {
        var output = await Exec(Container.Arkd,
            ["arkd", "note", "--amount", amountSats.ToString()], ct);
        return output.Trim();
    }

    /// <summary>
    /// Simulates an arkd operator signer-key rotation via the regtest node CLI
    /// (<c>node regtest/regtest.mjs rotate-signer</c>, added in ArkLabsHQ/arkade-regtest#30): a new
    /// active signer is generated and the current one is moved into the deprecated set with
    /// <paramref name="cutoff"/> (e.g. <c>"+86400"</c> = a migratable cutoff one day out). Blocks until
    /// arkd has re-synced and advertises the new signer set on <c>/v1/info</c>.
    /// </summary>
    public static async Task RotateSigner(string? cutoff = null, CancellationToken ct = default)
    {
        var regtestRoot = FindRegtestRoot();
        var args = new List<string> { "regtest/regtest.mjs", "rotate-signer" };
        if (cutoff is not null)
        {
            args.Add("--cutoff");
            args.Add(cutoff);
        }

        var result = await Cli.Wrap("node")
            .WithArguments(args)
            .WithWorkingDirectory(regtestRoot)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"regtest.mjs rotate-signer failed (exit={result.ExitCode}): " +
                $"{result.StandardError.Trim()} {result.StandardOutput.Trim()}");
    }

    /// <summary>
    /// Walks up from the test assembly directory to the SDK repo root — the directory that contains
    /// <c>regtest/regtest.mjs</c> — so the regtest CLI can be invoked regardless of the test's cwd.
    /// </summary>
    private static string FindRegtestRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "regtest", "regtest.mjs")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            $"Could not locate regtest/regtest.mjs by walking up from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Pays a BOLT11 invoice via the nigiri lnd node using lncli.
    /// </summary>
    public static async Task PayLndInvoice(string bolt11Invoice, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", Container.Lnd, "lncli", "--network=regtest", "payinvoice", "--force", bolt11Invoice])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"lncli payinvoice failed (exit={result.ExitCode}): {result.StandardError.Trim()}");
    }

    /// <summary>
    /// Sends BTC to an address via Bitcoin Core's bitcoin-cli.
    /// Returns the transaction ID.
    /// </summary>
    public static async Task<string> BitcoinSendToAddress(string address, Money amount, CancellationToken ct = default)
        => await BitcoinCli(["sendtoaddress", address,
            amount.ToDecimal(MoneyUnit.BTC).ToString("0.########", CultureInfo.InvariantCulture)], ct);

    /// <summary>
    /// Gets a new address from the Bitcoin Core wallet.
    /// </summary>
    public static async Task<string> BitcoinGetNewAddress(CancellationToken ct = default)
        => await BitcoinCli(["getnewaddress"], ct);

    /// <summary>
    /// Returns the current best block count from the Bitcoin regtest node.
    /// </summary>
    public static async Task<int> BitcoinGetBlockCount(CancellationToken ct = default)
    {
        var output = await BitcoinCli(["getblockcount"], ct);
        return int.Parse(output.Trim());
    }

    /// <summary>
    /// Returns the total BTC received by <paramref name="address"/> in transactions
    /// with at least <paramref name="minConf"/> confirmations. Returns <see cref="Money.Zero"/>
    /// when the address is unknown to the wallet (typical for freshly-derived taproot addresses).
    /// </summary>
    public static async Task<Money> BitcoinGetReceivedByAddress(
        string address, int minConf = 1, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", Container.Bitcoin, .. BitcoinCliArgs, "getreceivedbyaddress", address, minConf.ToString()])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (!result.IsSuccess)
            return Money.Zero;
        var btc = decimal.Parse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture);
        return Money.FromUnit(btc, MoneyUnit.BTC);
    }

    /// <summary>
    /// Forces a Boltz swap into <paramref name="status"/> using the best
    /// available method. Use <see cref="SubmarineSwapStatus"/> or <see cref="ChainSwapStatus"/>
    /// constants for the <paramref name="status"/> argument.
    /// <list type="number">
    /// <item>Tries <c>boltzr-cli swap set-status</c> — works for submarine swaps.</item>
    /// <item>Falls back to a direct <c>postgres</c> UPDATE on <c>"chainSwaps"</c>
    /// — required for chain swaps because the CLI only resolves submarine IDs.</item>
    /// </list>
    /// Returns <c>true</c> when either path succeeded; <c>false</c> when both fail
    /// (lets callers call <c>Assert.Ignore</c> instead of hard-failing).
    /// </summary>
    public static async Task<bool> TrySetBoltzSwapStatus(
        string swapId, string status, CancellationToken ct = default)
    {
        var cliResult = await Cli.Wrap("docker")
            .WithArguments([
                "exec", Container.Boltz,
                "/boltz-backend/target/release/boltzr-cli",
                "-c", "/home/boltz/.boltz/certificates",
                "swap", "set-status", swapId, status
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (cliResult.IsSuccess) return true;

        // boltzr-cli only resolves submarine swap IDs — chain swaps live in
        // "chainSwaps". Update the DB directly, then restart Boltz so its
        // in-memory nursery re-reads the new state from postgres. Without the
        // restart Boltz refuses cooperative refund requests because its nursery
        // still holds the old status in memory.
        var dbResult = await Cli.Wrap("docker")
            .WithArguments(["exec", Container.Postgres,
                "psql", "-U", "postgres", "-d", "boltz",
                "-v", $"sid={swapId}", "-v", $"status={status}",
                "-c", "UPDATE \"chainSwaps\" SET status = :'status' WHERE id = :'sid'"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (!dbResult.IsSuccess || !dbResult.StandardOutput.Contains("UPDATE 1"))
        {
            Console.WriteLine(
                $"[DockerHelper] TrySetBoltzSwapStatus {swapId}→{status} failed on both paths. " +
                $"CLI: {cliResult.StandardError.Trim()} | " +
                $"DB: {dbResult.StandardOutput.Trim()} {dbResult.StandardError.Trim()}");
            return false;
        }

        await RestartBoltzAndWait(ct);
        return true;
    }

    /// <summary>
    /// Returns the output index (vout) of the first output in <paramref name="txid"/>
    /// whose address matches <paramref name="address"/>. Requires the transaction to be
    /// a wallet transaction or txindex to be enabled on the Bitcoin Core node.
    /// </summary>
    public static async Task<int> BitcoinGetTxVout(string txid, string address, CancellationToken ct = default)
    {
        var json = await BitcoinCli(["getrawtransaction", txid, "1"], ct);
        using var doc = JsonDocument.Parse(json);
        foreach (var vout in doc.RootElement.GetProperty("vout").EnumerateArray())
        {
            var scriptPubKey = vout.GetProperty("scriptPubKey");
            if (scriptPubKey.TryGetProperty("address", out var addrEl) && addrEl.GetString() == address)
                return vout.GetProperty("n").GetInt32();
        }
        throw new InvalidOperationException($"Address {address} not found in outputs of tx {txid}");
    }

    /// <summary>
    /// Forces a BTC→ARK chain swap into <c>swap.expired</c> and sets the
    /// BTC-side lockup transaction ID in Boltz's DB so the cooperative refund
    /// path can retrieve the lockup tx hex from Boltz's status response after
    /// a restart. Mirrors <see cref="SetArkToBtcChainSwapExpiredWithLockup"/>
    /// for the opposite direction.
    /// </summary>
    /// <param name="swapId">Boltz swap ID.</param>
    /// <param name="lockupTxid">txid of the BTC transaction that funded the lockup address.</param>
    /// <param name="lockupVout">Output index of the lockup output within that transaction.</param>
    public static async Task SetBtcToArkChainSwapExpiredWithLockup(
        string swapId, string lockupTxid, int lockupVout, CancellationToken ct = default)
    {
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", Container.Postgres,
                "psql", "-U", "postgres", "-d", "boltz",
                "-c", $"UPDATE \"chainSwaps\" SET status = '{ChainSwapStatus.SwapExpired}' WHERE id = '{swapId}'"
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        // Setting transactionId+transactionVout for symbol='BTC' ensures Boltz includes
        // the lockup tx hex in the swap.expired status response after restart — without
        // this, CoopRefundBtcToArkChainSwap cannot find the outpoint to spend.
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", Container.Postgres,
                "psql", "-U", "postgres", "-d", "boltz",
                "-c", $"UPDATE \"chainSwapData\" SET \"transactionId\" = '{lockupTxid}', \"transactionVout\" = {lockupVout} WHERE \"swapId\" = '{swapId}' AND symbol = 'BTC'"
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        await RestartBoltzAndWait(ct);
    }

    /// <summary>
    /// Forces an ARK→BTC chain swap into <c>swap.expired</c> and manually sets the
    /// ARK-side lockup transaction ID in Boltz's DB — bypassing the normal swap flow.
    /// Call this while Boltz is already stopped; the method updates the DB and then
    /// starts Boltz and waits for it to be healthy.
    /// </summary>
    /// <param name="swapId">Boltz swap ID.</param>
    /// <param name="lockupTxid">txid of the ARK VTXO at the VHTLC (from arkd).</param>
    /// <param name="lockupVout">Output index of the VTXO within that virtual tx.</param>
    public static async Task SetArkToBtcChainSwapExpiredWithLockup(
        string swapId, string lockupTxid, int lockupVout, CancellationToken ct = default)
    {
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", Container.Postgres,
                "psql", "-U", "postgres", "-d", "boltz",
                "-c", $"UPDATE \"chainSwaps\" SET status = '{ChainSwapStatus.SwapExpired}' WHERE id = '{swapId}'"
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        await Cli.Wrap("docker")
            .WithArguments([
                "exec", Container.Postgres,
                "psql", "-U", "postgres", "-d", "boltz",
                "-c", $"UPDATE \"chainSwapData\" SET \"transactionId\" = '{lockupTxid}', \"transactionVout\" = {lockupVout} WHERE \"swapId\" = '{swapId}' AND symbol = 'ARK'"
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        await RestartBoltzAndWait(ct);
    }

    /// <summary>
    /// Restarts the Boltz container and waits until its REST API is healthy
    /// and the ARK/BTC chain pairs are loaded. Use after a direct DB status
    /// update so Boltz's in-memory nursery picks up the new swap state.
    /// </summary>
    public static async Task RestartBoltzAndWait(CancellationToken ct = default)
    {
        Console.WriteLine("[DockerHelper] Restarting Boltz container...");
        await StopContainer(Container.Boltz, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await StartContainer(Container.Boltz, ct);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var i = 1; i <= 60; i++)
        {
            try
            {
                var resp = await http.GetAsync("http://localhost:9069/v2/swap/chain", ct);
                if (resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[DockerHelper] Boltz ready (attempt {i})");
                    return;
                }
            }
            catch { /* not up yet */ }

            if (i < 60) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new InvalidOperationException("Boltz did not become healthy within 120 s after restart");
    }
    
    /// <summary>Docker container names used by the denigiri regtest stack.</summary>
    internal static class Container
    {
        public const string Bitcoin = "bitcoin";
        public const string Boltz = "boltz";
        public const string Lnd = "lnd";
        public const string BoltzLnd = "boltz-lnd";
        public const string Arkd = "arkd";
        public const string Fulmine = "boltz-fulmine";
        public const string Postgres = "postgres";
    }
}



/// <summary>
/// Boltz chain swap status strings as emitted on the WebSocket and REST API.
/// Use these constants instead of raw string literals so a Boltz API rename
/// shows up as a single compile-time change.
/// </summary>
public static class ChainSwapStatus
{
    public const string SwapCreated = "swap.created";
    public const string TransactionMempool = "transaction.mempool";                   // user's lockup in mempool
    public const string TransactionConfirmed = "transaction.confirmed";               // user's lockup confirmed
    public const string TransactionServerMempool = "transaction.server.mempool";      // cooperative claim
    public const string TransactionServerConfirmed = "transaction.server.confirmed";  // cooperative claim
    public const string TransactionClaimPending = "transaction.claim.pending";        // cooperative claim
    public const string TransactionClaimed = "transaction.claimed";                   // terminal — claim confirmed
    public const string TransactionLockupFailed = "transaction.lockupFailed";         // cooperative refund (or renegotiate via quote endpoint)
    public const string SwapExpired = "swap.expired";                                 // cooperative refund
    public const string TransactionFailed = "transaction.failed";                     // cooperative refund
    public const string TransactionRefunded = "transaction.refunded";                 // terminal — refund confirmed
}
