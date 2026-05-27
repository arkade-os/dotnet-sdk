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

    public static async Task MineBlocks(int count = 20, CancellationToken ct = default)
        => await Exec("bitcoin",
            ["bitcoin-cli", "-rpcwallet=", "-generate", count.ToString()], ct);

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
        var output = await Exec("ark",
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
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", "bitcoin", "bitcoin-cli", "-rpcwallet=", "sendtoaddress", address, btcAmount])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"bitcoin-cli sendtoaddress {address} {btcAmount} failed (exit={result.ExitCode}): {result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Gets a new address from the Bitcoin Core wallet.
    /// </summary>
    public static async Task<string> BitcoinGetNewAddress(CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", "bitcoin", "bitcoin-cli", "-rpcwallet=", "getnewaddress"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"bitcoin-cli getnewaddress failed (exit={result.ExitCode}): {result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Creates a BOLT 12 offer on the Core Lightning (<c>cln</c>) regtest container.
    /// Returns the <c>lno1…</c> offer string.
    /// </summary>
    /// <remarks>
    /// Requires a <c>cln</c> Docker container running Core Lightning with
    /// <c>lightning-cli</c> available at <c>/usr/local/bin/lightning-cli</c>.
    /// The regtest docker-compose stack must include CLN alongside LND for
    /// BOLT 12 end-to-end tests to pass.
    /// </remarks>
    /// <param name="amountSats">Amount in satoshis embedded in the offer; use <c>0</c> for an any-amount offer.</param>
    /// <param name="description">Short human-readable description included in the offer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The BOLT 12 offer string (<c>lno1…</c>).</returns>
    public static async Task<string> CreateClnBolt12Offer(
        long amountSats = 10000,
        string description = "test offer",
        CancellationToken ct = default)
    {
        var amountArg = amountSats > 0
            ? $"{amountSats * 1000}msat"  // CLN uses millisatoshis
            : "any";

        var output = await Exec("cln",
            ["lightning-cli", "--network=regtest", "offer", amountArg, description], ct);

        var offer = JsonSerializer.Deserialize<JsonObject>(output)?["bolt12"]
                        ?.GetValue<string>()
                    ?? throw new InvalidOperationException(
                        $"BOLT 12 offer creation on CLN failed. Output: {output}");
        return offer.Trim();
    }
}
