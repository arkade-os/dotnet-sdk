using System.Globalization;
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
    /// Creates an LND invoice and returns both the BOLT11 string and its
    /// hex-encoded payment hash. The hash is needed by
    /// <see cref="CancelLndInvoice"/> to deterministically fail the invoice
    /// before Boltz tries to pay it.
    /// </summary>
    public static async Task<(string PaymentRequest, string RHashHex)> CreateLndInvoiceWithHash(
        long amtSats = 10000, int expirySecs = 30, CancellationToken ct = default)
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
        var json = JsonSerializer.Deserialize<JsonObject>(output)
                   ?? throw new InvalidOperationException($"LND addinvoice returned non-JSON: {output}");

        var paymentRequest = json["payment_request"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Invoice creation on LND failed (no payment_request). Output: {output}");

        // LND returns r_hash as base64; lncli's cancelinvoice expects hex.
        var rHashB64 = json["r_hash"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Invoice creation on LND failed (no r_hash). Output: {output}");
        var rHashHex = Convert.ToHexString(Convert.FromBase64String(rHashB64)).ToLowerInvariant();

        return (paymentRequest.Trim(), rHashHex);
    }

    /// <summary>
    /// Cancels a pending LND invoice by payment hash. Once cancelled, any
    /// subsequent payment attempt to that invoice fails immediately with
    /// "invoice expired" — used by submarine-swap tests to deterministically
    /// drive Boltz into the <c>invoice.failedToPay</c> state without waiting
    /// for natural expiry.
    /// </summary>
    public static async Task CancelLndInvoice(string rHashHex, CancellationToken ct = default)
    {
        var output = await Exec("lnd",
            ["lncli", "--network=regtest", "cancelinvoice", rHashHex], ct);
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("lncli cancelinvoice returned empty output");
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
}
