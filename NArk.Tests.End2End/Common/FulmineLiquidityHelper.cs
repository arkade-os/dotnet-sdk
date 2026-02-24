using System.Text.Json.Nodes;
using Aspire.Hosting;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Ensures Fulmine has settled ARK VTXOs before tests that need Boltz ARK liquidity.
/// </summary>
public static class FulmineLiquidityHelper
{
    /// <summary>
    /// Polls Fulmine's balance, triggering block mining + settle until ARK VTXOs reach the minimum.
    /// Call this after Boltz is healthy but before tests that create BTC→ARK or reverse swaps.
    /// </summary>
    public static async Task EnsureArkLiquidity(DistributedApplication app, long minBalance = 200_000, int maxAttempts = 30)
    {
        var fulmineEndpoint = app.GetEndpoint("boltz-fulmine", "api");
        var fulmineHttp = new HttpClient { BaseAddress = new Uri(fulmineEndpoint.ToString()) };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var arkBalance = await GetFulmineArkBalance(fulmineHttp);
            Console.WriteLine($"[FulmineLiquidity] ARK balance: {arkBalance} sats (attempt {attempt}, need {minBalance})");
            if (arkBalance >= minBalance) return;

            // Mine blocks FIRST to confirm any pending boarding UTXOs,
            // THEN settle — arkd requires confirmed inputs.
            for (var i = 0; i < 3; i++)
                await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");

            try { await fulmineHttp.GetAsync("/api/v1/settle"); }
            catch { /* settle may fail if nothing to settle yet */ }

            // Wait for the arkd batch round to process the settle intent
            await Task.Delay(TimeSpan.FromSeconds(5));

            // Mine the batch commitment tx
            for (var i = 0; i < 3; i++)
                await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        var finalBalance = await GetFulmineArkBalance(fulmineHttp);
        Console.WriteLine($"[FulmineLiquidity] WARNING: Fulmine balance {finalBalance} still below {minBalance} after all attempts");
    }

    /// <summary>
    /// Retries an async operation that may fail with "insufficient liquidity",
    /// triggering block mining + Fulmine settle between attempts.
    /// </summary>
    public static async Task<T> RetryWithSettle<T>(DistributedApplication app, Func<Task<T>> action, int maxAttempts = 10)
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

                // Mine blocks FIRST (confirm boarding UTXOs), THEN settle
                for (var i = 0; i < 3; i++)
                    await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");

                try { await fulmineHttp.GetAsync("/api/v1/settle"); } catch { }

                // Wait for batch round + mine commitment tx
                await Task.Delay(TimeSpan.FromSeconds(5));
                for (var i = 0; i < 3; i++)
                    await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
                await Task.Delay(TimeSpan.FromSeconds(3));

                if (attempt == maxAttempts - 1) throw;
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    /// <summary>
    /// Gets Fulmine's offchain (ARK VTXO) balance in sats.
    /// The REST API returns { "offchain": N, "onchain": N, "total": N }.
    /// </summary>
    private static async Task<long> GetFulmineArkBalance(HttpClient fulmineHttp)
    {
        try
        {
            var balanceJson = await fulmineHttp.GetStringAsync("/api/v1/balance");
            Console.WriteLine($"[FulmineLiquidity] Raw balance response: {balanceJson}");
            var parsed = JsonNode.Parse(balanceJson);
            // Try REST fields first (offchain/onchain/total), fall back to gRPC field (amount)
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
