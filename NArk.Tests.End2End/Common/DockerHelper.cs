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
    /// Creates an LND invoice and returns the BOLT11 string. Used by tests
    /// that need a regular (non-hold) invoice that Boltz can pay through
    /// its standard pathway. To deterministically FAIL the payment use
    /// <see cref="StopContainer"/> on the LND node so Boltz can't reach it.
    /// </summary>
    public static async Task<string> CreateLndInvoiceForCancellation(
        long amtSats = 10000, int expirySecs = 3600, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "lncli", "--network=regtest", "addinvoice", "--amt", amtSats.ToString(CultureInfo.InvariantCulture),
            "--expiry", expirySecs.ToString(CultureInfo.InvariantCulture)
        };

        var output = await Exec("lnd", args.ToArray(), ct);
        var json = JsonSerializer.Deserialize<JsonObject>(output)
                   ?? throw new InvalidOperationException($"LND addinvoice returned non-JSON: {output}");

        return json["payment_request"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Invoice creation failed (no payment_request). Output: {output}");
    }

    /// <summary>
    /// Cancels a pending LND invoice by payment hash. Once cancelled, any
    /// subsequent payment attempt to that invoice fails immediately with
    /// "invoice expired" — used by submarine-swap tests to deterministically
    /// drive Boltz into the <c>invoice.failedToPay</c> state without waiting
    /// for natural expiry. <c>lncli cancelinvoice</c> returns empty stdout
    /// on success, so we just rely on the docker exec exit code (which
    /// <see cref="Exec"/> swallows via <see cref="CommandResultValidation.None"/>) —
    /// callers can verify the cancellation took effect by attempting payment
    /// or polling the invoice state.
    /// </summary>
    public static async Task CancelLndInvoice(string rHashHex, CancellationToken ct = default)
    {
        await Exec("lnd",
            ["lncli", "--network=regtest", "cancelinvoice", rHashHex], ct);
    }

    /// <summary>
    /// Adds an extra unspent VTXO to an already-funded wallet's existing
    /// receive contract by issuing another <c>ark send</c> from arkd. Used
    /// by concurrency tests where two parallel swaps must each lock their
    /// own VTXO without hitting <c>AlreadyLockedVtxoException</c>.
    /// </summary>
    /// <remarks>
    /// Must be invoked AFTER the wallet has at least one VTXO already (so a
    /// receive script is registered with arkd) and BEFORE the swap that
    /// needs the extra VTXO is initiated. Each call creates one additional
    /// <paramref name="amountSats"/>-sat VTXO at the same script. The
    /// supplied <paramref name="onVtxoArrived"/> callback is invoked when a
    /// new unspent VTXO lands at the receive script — typical use is a
    /// <see cref="TaskCompletionSource"/> the caller awaits.
    /// </remarks>
    public static async Task SendArkdNoteTo(string arkAddress, long amountSats,
        CancellationToken ct = default)
    {
        var result = await CliWrap.Cli.Wrap("docker")
            .WithArguments([
                "exec", "ark", "ark", "send", "--to", arkAddress,
                "--amount", amountSats.ToString(),
                "--password", "secret"
            ])
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"ark send to {arkAddress} for {amountSats} sats failed (exit={result.ExitCode}): " +
                $"stdout={result.StandardOutput.Trim()}, stderr={result.StandardError.Trim()}");
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
