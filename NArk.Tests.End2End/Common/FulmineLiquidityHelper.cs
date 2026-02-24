using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;

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
            await FundFulmineBoarding(app, fulmineHttp);

            // Mine blocks to confirm the boarding UTXO (arkd requires confirmed inputs)
            for (var i = 0; i < 6; i++)
                await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Now settle — boarding UTXO is confirmed
            try { await fulmineHttp.GetAsync("/api/v1/settle"); }
            catch { /* settle may fail if nothing to settle yet */ }

            // Wait for the arkd batch round to process the settle intent
            await Task.Delay(TimeSpan.FromSeconds(10));

            // Mine the batch commitment tx
            for (var i = 0; i < 6; i++)
                await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
            await Task.Delay(TimeSpan.FromSeconds(3));
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
                await FundFulmineBoarding(app, fulmineHttp);

                for (var i = 0; i < 6; i++)
                    await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
                await Task.Delay(TimeSpan.FromSeconds(2));

                try { await fulmineHttp.GetAsync("/api/v1/settle"); } catch { }

                await Task.Delay(TimeSpan.FromSeconds(10));
                for (var i = 0; i < 6; i++)
                    await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
                await Task.Delay(TimeSpan.FromSeconds(3));

                if (attempt == maxAttempts - 1) throw;
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    /// <summary>
    /// Sends 1 BTC to Fulmine's boarding address via the chopsticks faucet.
    /// </summary>
    private static async Task FundFulmineBoarding(DistributedApplication app, HttpClient fulmineHttp)
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

            var chopsticksEndpoint = app.GetEndpoint("chopsticks", "http");
            var response = await new HttpClient().PostAsJsonAsync($"{chopsticksEndpoint}/faucet", new
            {
                amount = 1,
                address = onchainAddress
            });
            Console.WriteLine($"[FulmineLiquidity] Faucet response: {response.StatusCode}");
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
}
