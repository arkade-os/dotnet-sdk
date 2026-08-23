using System.Text.Json.Nodes;
using NBitcoin;
using NArk.Tests.End2End.Swaps;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Ensures Fulmine has settled ARK VTXOs before tests that need Boltz ARK liquidity.
/// </summary>
public static class FulmineLiquidityHelper
{
    /// <summary>
    /// Ensures Fulmine has enough *spendable* ARK VTXOs by settling (which renews
    /// expired/recoverable VTXOs) and, when genuinely underfunded, boarding fresh BTC first.
    /// Call this after Boltz is healthy but before tests that create BTC→ARK or reverse swaps.
    /// </summary>
    /// <remarks>
    /// The raw <c>/api/v1/balance</c> amount also counts VTXOs that have passed their
    /// renewal deadline — Fulmine reports them in the balance but a plain offchain send
    /// fails with <c>{"code":2,"message":"missing vtxos"}</c>. VTXOs on the regtest stack
    /// live only ~50 minutes, so a long-lived stack hits this constantly. Gate on the
    /// spendable subset (from <c>/api/v1/vtxos</c>) instead, and recover via settle:
    /// a settle round re-anchors the expired funds into a fresh spendable set.
    /// </remarks>
    public static async Task EnsureArkLiquidity(long minBalance = 200_000, int maxAttempts = 20)
    {
        var fulmineHttp = new HttpClient { BaseAddress = new Uri(SharedSwapInfrastructure.FulmineEndpoint.ToString()) };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var rawBalance = await GetFulmineArkBalance(fulmineHttp);
            var spendable = await GetFulmineSpendableBalance(fulmineHttp);
            // Spendable probe is best-effort: if /api/v1/vtxos is unavailable, fall
            // back to the raw balance (the original behaviour).
            var effective = spendable >= 0 ? spendable : rawBalance;
            Console.WriteLine($"[FulmineLiquidity] ARK balance: spendable={spendable} raw={rawBalance} sats (attempt {attempt}, need {minBalance})");
            if (effective >= minBalance) return;

            if (effective < minBalance)
            {
                // Board fresh BTC before settling.
                await FundFulmineBoarding(fulmineHttp);

                // Mine blocks to confirm the boarding UTXO (arkd requires confirmed inputs)
                for (var i = 0; i < 6; i++)
                    await DockerHelper.MineBlocks();
                await Task.Delay(TimeSpan.FromSeconds(2));

                // Log raw balance after mining (to see if onchain increased)
                await LogRawBalance(fulmineHttp, "after-mine");
            }

            // Settle — renews expired/recoverable VTXOs into a fresh spendable set
            // and absorbs any just-boarded UTXO.
            await SettleWithLogging(fulmineHttp);

            // Wait for the arkd batch round to process the settle intent
            await Task.Delay(TimeSpan.FromSeconds(15));

            // Mine the batch commitment tx
            for (var i = 0; i < 6; i++)
                await DockerHelper.MineBlocks();
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
    public static async Task<T> RetryWithSettle<T>(Func<Task<T>> action, int maxAttempts = 5)
    {
        var fulmineHttp = new HttpClient { BaseAddress = new Uri(SharedSwapInfrastructure.FulmineEndpoint.ToString()) };

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
                    await DockerHelper.MineBlocks();
                await Task.Delay(TimeSpan.FromSeconds(2));

                await LogRawBalance(fulmineHttp, $"retry-{attempt}-after-mine");
                await SettleWithLogging(fulmineHttp);

                await Task.Delay(TimeSpan.FromSeconds(15));
                for (var i = 0; i < 6; i++)
                    await DockerHelper.MineBlocks();
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

            var output = await DockerHelper.BitcoinSendToAddress(onchainAddress, Money.Coins(1));
            Console.WriteLine($"[FulmineLiquidity] sendtoaddress txid: {output}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FulmineLiquidity] Funding failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sums Fulmine's *spendable* VTXOs in sats: not spent, not swept, and not
    /// within a minute of their renewal deadline (a near-expiry VTXO can lapse
    /// mid-flow). Returns -1 when the <c>/api/v1/vtxos</c> endpoint is
    /// unavailable or unparseable, so callers can fall back to the raw balance.
    /// </summary>
    private static async Task<long> GetFulmineSpendableBalance(HttpClient fulmineHttp)
    {
        try
        {
            var vtxosJson = await fulmineHttp.GetStringAsync("/api/v1/vtxos");
            var vtxos = JsonNode.Parse(vtxosJson)?["vtxos"]?.AsArray();
            if (vtxos is null) return -1;

            var cutoff = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
            long spendable = 0;
            foreach (var vtxo in vtxos)
            {
                if (vtxo?["isSpent"]?.GetValue<bool>() == true) continue;
                if (vtxo?["isSwept"]?.GetValue<bool>() == true) continue;
                if (!long.TryParse(vtxo?["expiresAt"]?.ToString(), out var expiresAt) || expiresAt <= cutoff) continue;
                if (long.TryParse(vtxo?["amount"]?.ToString(), out var amount))
                    spendable += amount;
            }
            return spendable;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FulmineLiquidity] Spendable-balance check failed: {ex.Message}");
            return -1;
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
