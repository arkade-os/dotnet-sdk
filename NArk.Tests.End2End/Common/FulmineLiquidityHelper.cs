using System.Text.Json.Nodes;
using Aspire.Hosting;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Ensures Fulmine has settled ARK VTXOs before tests that need Boltz ARK liquidity.
/// </summary>
public static class FulmineLiquidityHelper
{
    /// <summary>
    /// Polls Fulmine's balance, triggering settle + block mining until ARK VTXOs are available.
    /// Call this after Boltz is healthy but before tests that create BTC→ARK or reverse swaps.
    /// </summary>
    public static async Task EnsureArkLiquidity(DistributedApplication app, int maxAttempts = 15)
    {
        var fulmineEndpoint = app.GetEndpoint("boltz-fulmine", "api");
        var fulmineHttp = new HttpClient { BaseAddress = new Uri(fulmineEndpoint.ToString()) };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var balanceJson = await fulmineHttp.GetStringAsync("/api/v1/balance");
                var balance = JsonNode.Parse(balanceJson)?["amount"];
                var arkBalance = long.TryParse(balance?.ToString(), out var b) ? b : 0;
                Console.WriteLine($"[FulmineLiquidity] ARK balance: {arkBalance} sats (attempt {attempt})");
                if (arkBalance > 0)
                {
                    // Log Boltz pair limits for diagnostics
                    try
                    {
                        var boltzProxy = app.GetEndpoint("boltz-proxy", "api");
                        var boltzHttp = new HttpClient { BaseAddress = new Uri(boltzProxy.ToString()) };
                        var pairsJson = await boltzHttp.GetStringAsync("/v2/swap/reverse");
                        Console.WriteLine($"[FulmineLiquidity] Boltz reverse pairs at setup: {pairsJson}");
                    }
                    catch (Exception pairsEx)
                    {
                        Console.WriteLine($"[FulmineLiquidity] Boltz reverse pairs query failed at setup: {pairsEx.Message}");
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FulmineLiquidity] Balance check failed (attempt {attempt}): {ex.Message}");
            }

            try { await fulmineHttp.GetAsync("/api/v1/settle"); }
            catch { /* settle may fail if nothing to settle yet */ }

            for (var i = 0; i < 3; i++)
                await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Console.WriteLine("[FulmineLiquidity] WARNING: Fulmine still has no ARK balance after all attempts");
    }

    /// <summary>
    /// Retries an async operation that may fail with "insufficient liquidity",
    /// triggering Fulmine settle + block mining between attempts.
    /// </summary>
    public static async Task<T> RetryWithSettle<T>(DistributedApplication app, Func<Task<T>> action, int maxAttempts = 10)
    {
        var fulmineEndpoint = app.GetEndpoint("boltz-fulmine", "api");
        var fulmineHttp = new HttpClient { BaseAddress = new Uri(fulmineEndpoint.ToString()) };
        var boltzProxy = app.GetEndpoint("boltz-proxy", "api");
        var boltzHttp = new HttpClient { BaseAddress = new Uri(boltzProxy.ToString()) };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("insufficient liquidity"))
            {
                // Diagnostic: query Fulmine balance and Boltz reverse pairs limits
                try
                {
                    var balanceJson = await fulmineHttp.GetStringAsync("/api/v1/balance");
                    Console.WriteLine($"[FulmineLiquidity] Attempt {attempt}: insufficient liquidity. Fulmine balance: {balanceJson}");
                }
                catch (Exception balEx)
                {
                    Console.WriteLine($"[FulmineLiquidity] Attempt {attempt}: insufficient liquidity. Fulmine balance query failed: {balEx.Message}");
                }

                try
                {
                    var pairsJson = await boltzHttp.GetStringAsync("/v2/swap/reverse");
                    Console.WriteLine($"[FulmineLiquidity] Boltz reverse pairs: {pairsJson}");
                }
                catch (Exception pairsEx)
                {
                    Console.WriteLine($"[FulmineLiquidity] Boltz reverse pairs query failed: {pairsEx.Message}");
                }

                try { await fulmineHttp.GetAsync("/api/v1/settle"); } catch { }
                for (var i = 0; i < 3; i++)
                    await app.ResourceCommands.ExecuteCommandAsync("bitcoin", "generate-blocks");
                await Task.Delay(TimeSpan.FromSeconds(5));

                if (attempt == maxAttempts - 1) throw;
            }
        }

        throw new InvalidOperationException("Unreachable");
    }
}
