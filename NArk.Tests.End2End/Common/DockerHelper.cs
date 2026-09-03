using System.Globalization;
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

    /// <summary>
    /// Mines <paramref name="count"/> regtest blocks (default 1).
    /// </summary>
    public static async Task MineBlocks(int count = 1, CancellationToken ct = default)
        => await Exec(Container.Bitcoin, [.. BitcoinCliArgs, "-generate", count.ToString()], ct);

    /// <summary>
    /// Mines however many blocks are needed to reach <paramref name="targetHeight"/>.
    /// No-ops if the chain is already at or beyond the target.
    /// Used by CLTV / timelock tests that need a specific block height before an
    /// absolute-locktime script path becomes spendable.
    /// </summary>
    public static async Task MineRegtestBlocksToHeight(int targetHeight, CancellationToken ct = default)
    {
        var current = await BitcoinGetBlockCount(ct);
        if (current >= targetHeight) return;
        await MineBlocks(targetHeight - current, ct);
    }

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
    /// Returns the regtest tip's median time past (BIP 113) — the clock
    /// consensus uses for time-based locks (BIP-68 relative time locks, CLTV).
    /// </summary>
    public static async Task<DateTimeOffset> BitcoinGetMedianTime(CancellationToken ct = default)
    {
        var json = JsonNode.Parse(await BitcoinCli(["getblockchaininfo"], ct))!;
        return DateTimeOffset.FromUnixTimeSeconds(json["mediantime"]!.GetValue<long>());
    }

    /// <summary>
    /// Pins the node's clock to <paramref name="time"/> (<c>setmocktime</c>), or
    /// releases it back to the system clock when <paramref name="time"/> is null.
    /// <para>
    /// Blocks mined afterwards carry the mocked timestamp, which is the only way
    /// to advance median time past far enough to mature a BIP-68 <i>time-based</i>
    /// relative lock in regtest — mining alone can't, since block times track the
    /// real clock. Pair with <see cref="AdvanceMedianTimePast"/>.
    /// </para>
    /// </summary>
    public static async Task BitcoinSetMockTime(DateTimeOffset? time, CancellationToken ct = default)
        => await BitcoinCli(["setmocktime", (time?.ToUnixTimeSeconds() ?? 0).ToString()], ct);

    /// <summary>
    /// Pushes the regtest tip's median time past to at least
    /// <paramref name="target"/> by mocking the node clock and mining.
    /// <para>
    /// Median time past is the median of the last 11 block times, so 11 blocks
    /// at the mocked timestamp are enough to move it there wholesale. The mock
    /// is left in place — callers should reset it with
    /// <c>BitcoinSetMockTime(null)</c> once done, since it freezes the node's
    /// clock for everything else running against the same container.
    /// </para>
    /// </summary>
    public static async Task AdvanceMedianTimePast(DateTimeOffset target, CancellationToken ct = default)
    {
        await BitcoinSetMockTime(target, ct);
        await MineBlocks(11, ct);
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
    
    

    /// <summary>Docker container names used by the denigiri regtest stack.</summary>
    internal static class Container
    {
        public const string Bitcoin = "bitcoin";
        public const string Boltz = "boltz";
        public const string Lnd = "lnd";
        public const string BoltzLnd = "boltz-lnd";
        public const string Arkd = "arkd";
        public const string Postgres = "postgres";
    }
}