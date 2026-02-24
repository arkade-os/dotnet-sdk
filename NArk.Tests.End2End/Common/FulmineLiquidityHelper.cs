using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Ensures Fulmine has settled ARK VTXOs before tests that need Boltz ARK liquidity.
/// </summary>
public static class FulmineLiquidityHelper
{
    /// <summary>
    /// Ensures Fulmine has enough ARK VTXOs by funding its boarding address, mining, and settling.
    /// Call this after Boltz is healthy but before tests that create BTC→ARK or reverse swaps.
    /// </summary>
    public static async Task EnsureArkLiquidity(DistributedApplication app, long minBalance = 200_000, int maxAttempts = 20)
    {
        var fulmineEndpoint = app.GetEndpoint("boltz-fulmine", "api");
        var fulmineHttp = new HttpClient { BaseAddress = new Uri(fulmineEndpoint.ToString()) };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var arkBalance = await GetFulmineArkBalance(fulmineHttp);
            Console.WriteLine($"[FulmineLiquidity] ARK balance: {arkBalance} sats (attempt {attempt}, need {minBalance})");
            if (arkBalance >= minBalance) return;

            // Fund Fulmine's boarding address with fresh BTC each attempt
            await FundFulmineBoarding(fulmineHttp);

            // Mine blocks to confirm the boarding UTXO (arkd requires confirmed inputs)
            for (var i = 0; i < 6; i++)
                await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Log raw balance after mining (to see if onchain increased)
            await LogRawBalance(fulmineHttp, "after-mine");

            // Now settle — boarding UTXO is confirmed
            await SettleWithLogging(fulmineHttp);

            // Wait for the arkd batch round to process the settle intent
            await Task.Delay(TimeSpan.FromSeconds(15));

            // Mine the batch commitment tx
            for (var i = 0; i < 6; i++)
                await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Log balance after full cycle
            await LogRawBalance(fulmineHttp, "after-settle-cycle");
        }

        var finalBalance = await GetFulmineArkBalance(fulmineHttp);
        Console.WriteLine($"[FulmineLiquidity] WARNING: Fulmine balance {finalBalance} still below {minBalance} after all attempts");
    }

    /// <summary>
    /// Retries an async operation that may fail with "insufficient liquidity",
    /// re-funding Fulmine and settling between attempts.
    /// </summary>
    public static async Task<T> RetryWithSettle<T>(DistributedApplication app, Func<Task<T>> action, int maxAttempts = 5)
    {
        var fulmineEndpoint = app.GetEndpoint("boltz-fulmine", "api");
        var fulmineHttp = new HttpClient { BaseAddress = new Uri(fulmineEndpoint.ToString()) };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("insufficient liquidity"))
            {
                var balance = await GetFulmineArkBalance(fulmineHttp);
                Console.WriteLine($"[FulmineLiquidity] Attempt {attempt}: insufficient liquidity. Fulmine ARK balance: {balance} sats");

                // Re-fund and settle
                await FundFulmineBoarding(fulmineHttp);

                for (var i = 0; i < 6; i++)
                    await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
                await Task.Delay(TimeSpan.FromSeconds(2));

                await LogRawBalance(fulmineHttp, $"retry-{attempt}-after-mine");
                await SettleWithLogging(fulmineHttp);

                await Task.Delay(TimeSpan.FromSeconds(15));
                for (var i = 0; i < 6; i++)
                    await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
                await Task.Delay(TimeSpan.FromSeconds(3));
                await LogRawBalance(fulmineHttp, $"retry-{attempt}-after-settle-cycle");

                if (attempt == maxAttempts - 1) throw;
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    /// <summary>
    /// Sends 1 BTC to Fulmine's boarding address via bitcoin-cli sendtoaddress.
    /// Uses Docker exec to call Bitcoin Core directly (chopsticks faucet is inaccessible from test runner).
    /// </summary>
    private static async Task FundFulmineBoarding(HttpClient fulmineHttp)
    {
        try
        {
            var addressJson = await fulmineHttp.GetStringAsync("/api/v1/address");
            var arkAddress = JsonNode.Parse(addressJson)?["address"]?.GetValue<string>();
            if (string.IsNullOrEmpty(arkAddress))
            {
                Console.WriteLine("[FulmineLiquidity] Could not get Fulmine address");
                return;
            }

            var onchainAddress = new Uri(arkAddress).AbsolutePath;
            Console.WriteLine($"[FulmineLiquidity] Funding boarding address: {onchainAddress}");

            var result = await Cli.Wrap("docker")
                .WithArguments(["exec", "bitcoin", "bitcoin-cli", "-rpcwallet=", "sendtoaddress", onchainAddress, "1"])
                .ExecuteBufferedAsync();
            Console.WriteLine($"[FulmineLiquidity] sendtoaddress txid: {result.StandardOutput.Trim()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FulmineLiquidity] Funding failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets Fulmine's ARK VTXO balance in sats.
    /// </summary>
    private static async Task<long> GetFulmineArkBalance(HttpClient fulmineHttp)
    {
        try
        {
            var balanceJson = await fulmineHttp.GetStringAsync("/api/v1/balance");
            var parsed = JsonNode.Parse(balanceJson);
            var balance = parsed?["offchain"] ?? parsed?["amount"];
            return long.TryParse(balance?.ToString(), out var b) ? b : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FulmineLiquidity] Balance check failed: {ex.Message}");
            return 0;
        }
    }

    private static async Task LogRawBalance(HttpClient fulmineHttp, string label)
    {
        try
        {
            var raw = await fulmineHttp.GetStringAsync("/api/v1/balance");
            Console.WriteLine($"[FulmineLiquidity] {label} raw balance: {raw}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FulmineLiquidity] {label} balance check failed: {ex.Message}");
        }
    }

    private static async Task SettleWithLogging(HttpClient fulmineHttp)
    {
        try
        {
            var resp = await fulmineHttp.GetAsync("/api/v1/settle");
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[FulmineLiquidity] settle response: {resp.StatusCode} body={body}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FulmineLiquidity] settle failed: {ex.Message}");
        }
    }
}
