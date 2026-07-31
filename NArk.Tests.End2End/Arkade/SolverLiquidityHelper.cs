using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.CoinSelector;
using NArk.Core.Services;
using NArk.Tests.End2End.Common;

namespace NArk.Tests.End2End.Arkade;

/// <summary>
/// Runtime equivalent of <c>solver-init</c> for tests that need a filled solver: mints a fresh
/// 0-decimals asset from a throwaway funded wallet, sends the whole supply to the solver's offchain
/// address, and registers the <c>BTC/&lt;asset&gt;</c> (+ reverse) pair against the existing mock
/// pricefeed. Unlike <c>SOLVER_INIT_ASSET_FUNDING</c> (a fixed 50k that a single ~49_750 fill drains),
/// this tops the solver up with as much inventory as the test needs, so several BTC→asset fills can
/// coexist in one run.
/// </summary>
/// <remarks>
/// The mock feed <c>http://pricefeed/btc-asset</c> is static and asset-agnostic (it returns
/// <c>1e-8</c> for any pair), so the fresh pair reuses it directly — the solver resolves it internally
/// on its own docker network. The 1 sat ↔ 1 unit pricing the tests rely on holds only because the
/// asset is issued with <c>decimals=0</c>; the solver reads that from the arkd indexer (issuance
/// metadata), which is why it is set at issuance rather than on the pair.
/// </remarks>
internal static class SolverLiquidityHelper
{
    private const string PriceFeedBase = "http://pricefeed";

    /// <summary>
    /// Ensures the solver holds a freshly-minted asset it can pay out, and returns the asset id.
    /// The registered pair is <c>BTC/&lt;assetId&gt;</c>; discover its limits via
    /// <c>SolverClient.ListPairsAsync</c> if needed.
    /// </summary>
    /// <param name="solverEndpoint">The solver's grpc-gateway REST base (e.g. http://localhost:7091).</param>
    /// <param name="inventory">Asset units to mint and hand to the solver. Each 0-decimals unit is
    /// backed by a sat carrier, so the throwaway wallet is funded with <paramref name="inventory"/>
    /// plus headroom for fees.</param>
    internal static async Task<string> EnsureAssetMarket(
        Uri solverEndpoint,
        ulong inventory = 500_000,
        CancellationToken ct = default)
    {
        // A dedicated funder wallet (not the test's maker) — it needs enough BTC to back every asset
        // unit's sat carrier plus fees.
        var funder = await FundedWalletHelper.GetFundedWallet(
            vtxoCount: 1, amountSatsPerVtxo: checked((int)(inventory + 500_000)));

        var (assetManager, coinService, intentStorage) = AssetTestHelpers.CreateAssetServices(funder);

        // 1. Mint the asset. decimals=0 MUST be set here — the solver reads it from the indexer to
        //    validate the pair and price offers; a wrong value silently rejects every fill.
        var issuance = await assetManager.IssueAsync(funder.walletIdentifier,
            new IssuanceParams(
                Amount: inventory,
                Metadata: new Dictionary<string, string>
                {
                    ["decimals"] = "0",
                    ["ticker"] = "RGT",
                    ["name"] = "Regtest Asset",
                }),
            ct);
        var assetId = issuance.AssetId;
        await AssetTestHelpers.PollUntilAssetVtxo(funder, assetId, TimeSpan.FromSeconds(60));

        // 2. Send the whole supply to the solver's offchain address.
        using var http = new HttpClient { BaseAddress = solverEndpoint };
        var solverAddress =
            JsonNode.Parse(await http.GetStringAsync("v1/address", ct))?["offchain_address"]?.GetValue<string>()
            ?? throw new InvalidOperationException("solver /v1/address returned no offchain_address");

        var serverInfo = await funder.clientTransport.GetServerInfoAsync(ct);
        var spending = new SpendingService(
            funder.vtxoStorage, funder.contracts, funder.walletProvider, coinService, funder.contractService,
            funder.clientTransport, new DefaultCoinSelector(), funder.safetyService, intentStorage);

        await spending.Spend(funder.walletIdentifier,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, ArkAddress.Parse(solverAddress))
            {
                Assets = [new ArkTxOutAsset(assetId, inventory)],
            },
        ], ct);

        // 3. Register both directions on the existing mock feed.
        await RegisterPair(http, $"BTC/{assetId}", $"{PriceFeedBase}/btc-asset", ct);
        await RegisterPair(http, $"{assetId}/BTC", $"{PriceFeedBase}/asset-btc", ct);

        // 4. Wait until the solver actually reports the inventory — this is what proves the send landed
        //    AND that the decimals reached it (a rejected send/pair never shows up here).
        var solver = new SolverClient(solverEndpoint);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var held = (await solver.GetAssetBalancesAsync(ct)).GetValueOrDefault(assetId);
            if (held >= inventory - inventory / 10) // allow ≤10% for round/dust accounting
                return assetId;
            await Task.Delay(1000, ct);
        }

        throw new TimeoutException(
            $"solver did not report inventory for freshly-funded asset {assetId} within 90s " +
            "(check the send confirmed and the pair registered with the right decimals)");
    }

    private static async Task RegisterPair(HttpClient http, string pair, string priceFeed, CancellationToken ct)
    {
        var body = new { pair = new { pair, min_amount = 1, max_amount = 100_000_000, price_feed = priceFeed } };
        var resp = await http.PostAsJsonAsync("v1/pair", body, ct);
        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Conflict)
            return;

        // A re-registered pair is fine; only a genuine server error should fail the helper.
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!text.Contains("exist", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"POST /v1/pair '{pair}' failed ({(int)resp.StatusCode}): {text}");
    }
}
