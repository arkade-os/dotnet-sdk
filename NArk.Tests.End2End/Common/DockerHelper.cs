using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWrap;
using CliWrap.Buffered;

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
            .WithArguments(["exec", "bitcoin", .. BitcoinCliArgs, .. args])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"bitcoin-cli {string.Join(' ', args)} failed (exit={result.ExitCode}): {result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
    }

    public static async Task MineBlocks(int count = 20, CancellationToken ct = default)
        => await Exec("bitcoin", [.. BitcoinCliArgs, "-generate", count.ToString()], ct);

    /// <summary>
    /// Drives a Boltz swap into a specific status on demand via the
    /// boltzr-cli admin tool baked into the Boltz container. Only
    /// <c>invoice.failedToPay</c> and <c>invoice.pending</c> are accepted —
    /// any other value throws on the Boltz side.
    /// </summary>
    /// <remarks>
    /// Setting <c>invoice.failedToPay</c> writes the swap's failure reason
    /// to "payment has been cancelled" and fires the same nursery event +
    /// websocket update the production failure path emits, so an SDK
    /// listening to the websocket sees an indistinguishable
    /// <c>invoice.failedToPay</c> and follows its cooperative-refund flow.
    /// Source: <c>BoltzExchange/boltz-backend lib/service/Service.ts</c>.
    /// </remarks>
    public static async Task SetBoltzSwapStatus(string swapId, string status, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments([
                "exec", "boltz",
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

        var output = await Exec("lnd", args.ToArray(), ct);
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
        var output = await Exec("arkd",
            ["arkd", "note", "--amount", amountSats.ToString()], ct);
        return output.Trim();
    }

    /// <summary>
    /// Pays a BOLT11 invoice via the nigiri lnd node using lncli.
    /// </summary>
    public static async Task PayLndInvoice(string bolt11Invoice, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", "lnd", "lncli", "--network=regtest", "payinvoice", "--force", bolt11Invoice])
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
    public static async Task<string> BitcoinSendToAddress(string address, string btcAmount, CancellationToken ct = default)
        => await BitcoinCli(["sendtoaddress", address, btcAmount], ct);

    /// <summary>
    /// Gets a new address from the Bitcoin Core wallet.
    /// </summary>
    public static async Task<string> BitcoinGetNewAddress(CancellationToken ct = default)
        => await BitcoinCli(["getnewaddress"], ct);
}
