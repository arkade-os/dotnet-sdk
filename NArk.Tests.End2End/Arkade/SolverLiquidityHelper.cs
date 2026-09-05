using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.CoinSelector;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Tests.End2End.Common;
using NBitcoin.Secp256k1;

namespace NArk.Tests.End2End.Arkade;

/// <summary>
/// Runtime equivalent of <c>solver-init</c> for tests that need a filled solver: mints a fresh
/// 0-decimals asset from a throwaway funded wallet, sends the whole supply to the solver's offchain
/// address, and registers the <c>BTC/&lt;asset&gt;</c> market — both directions in one call — against
/// the existing mock pricefeed. Unlike <c>SOLVER_INIT_ASSET_FUNDING</c> (a fixed 50k that a single ~49_750 fill drains),
/// this tops the solver up with as much inventory as the test needs, so several BTC→asset fills can
/// coexist in one run.
/// </summary>
/// <remarks>
/// The mock feed <c>http://pricefeed/btc-asset</c> is static and asset-agnostic (it returns
/// <c>1e-8</c> for any pair), so the fresh market reuses it directly — the solver resolves it internally
/// on its own docker network. The 1 sat ↔ 1 unit pricing the tests rely on holds only because the
/// asset is issued with <c>decimals=0</c>; the solver reads that from the arkd indexer (issuance
/// metadata), which is why it is set at issuance rather than on the pair.
/// </remarks>
internal static class SolverLiquidityHelper
{
    private const string PriceFeedBase = "http://pricefeed";

    /// <summary>
    /// Ensures the solver holds a freshly-minted asset it can pay out, and returns the asset id.
    /// The registered market is <c>BTC/&lt;assetId&gt;</c>; discover its limits via
    /// <c>SolverClient.ListPairsAsync</c> if needed.
    /// </summary>
    /// <param name="solverEndpoint">The solver's grpc-gateway REST base (e.g. http://localhost:7091).</param>
    /// <param name="inventory">Asset units to mint and hand to the solver. Each 0-decimals unit is
    /// backed by a sat carrier, so the throwaway wallet is funded with <paramref name="inventory"/>
    /// plus headroom for fees.</param>
    /// <param name="btcLiquidity">
    /// Sats to hand the solver as a dedicated, unencumbered VTXO so it can pay out the
    /// <c>asset→BTC</c> direction. See the remarks on why this is separate from the asset send.
    /// </param>
    internal static async Task<string> EnsureAssetMarket(
        Uri solverEndpoint,
        ulong inventory = 500_000,
        ulong btcLiquidity = 1_000_000,
        CancellationToken ct = default)
    {
        // A dedicated funder wallet (not the test's maker) — it needs enough BTC to back every asset
        // unit's sat carrier, the solver's BTC float, plus fees.
        var funder = await FundedWalletHelper.GetFundedWallet(
            vtxoCount: 1, amountSatsPerVtxo: checked((int)(inventory + btcLiquidity + 500_000)));

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

        // Both outputs go out in ONE spend. Two back-to-back Spend calls race the safety
        // service's VTXO lock — the funder's coin is still held by the first when the second
        // selects coins, which throws AlreadyLockedVtxoException.
        //
        // The second output is the solver's BTC float. The asset→BTC direction needs the solver
        // to pay *sats* out, and unlike its asset inventory nothing in the test tops that up:
        // solver-init seeds SOLVER_INIT_BTC once, and by the time a reverse leg runs those sats
        // are bound up in VTXOs the solver is settling. In CI an asset→BTC offer published at
        // 14:26:17 was not filled until 14:30:17, sitting through two full settlement rounds,
        // while every BTC→asset offer in the same run filled in 1–3s (those pay from asset
        // inventory). Its own unencumbered BTC VTXO removes that dependency.
        var solverArkAddress = ArkAddress.Parse(solverAddress);
        await spending.Spend(funder.walletIdentifier,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, solverArkAddress)
            {
                Assets = [new ArkTxOutAsset(assetId, inventory)],
            },
            new ArkTxOut(ArkTxOutType.Vtxo, NBitcoin.Money.Satoshis(btcLiquidity), solverArkAddress),
        ], ct);

        // 3. Register the market on the existing mock feed. One call covers both directions:
        //    solverd takes a base/quote pair with bounds on each side, and a side is enabled by
        //    having a non-zero maximum. The old API took a direction per registration.
        await RegisterMarket(http, "BTC", assetId, $"{PriceFeedBase}/btc-asset", "/btc/asset", ct);

        // 4. Wait until both sends have landed. The asset side is proved by the solver's own
        //    balance API — that also proves the decimals reached it, since a rejected send or a
        //    mis-decimalled pair never shows up there. The solver reports no sat balance
        //    (`/v1/balance` returns asset_balances only), so the BTC side is proved from arkd:
        //    an unspent VTXO of the expected size on the solver's own address.
        var solverScript = solverArkAddress.ScriptPubKey.ToHex();
        var solver = new SolverClient(solverEndpoint);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var held = (await solver.GetAssetBalancesAsync(ct)).GetValueOrDefault(assetId);
            if (held >= inventory - inventory / 10 // allow ≤10% for round/dust accounting
                && await HasSpendableSats(funder.clientTransport, solverScript, btcLiquidity, ct))
            {
                return assetId;
            }
            await Task.Delay(1000, ct);
        }

        throw new TimeoutException(
            $"solver did not report inventory for freshly-funded asset {assetId} plus a spendable " +
            $"{btcLiquidity}-sat VTXO within 90s (check the sends confirmed and the pair registered " +
            "with the right decimals)");
    }

    /// <summary>
    /// Makes sure a solver holds an unencumbered VTXO it can pay a swap out of, funding one if not.
    /// </summary>
    /// <param name="serverKey">The Arkade server's signer key, to rebuild the solver's address.</param>
    /// <param name="solverPkScriptHex">The solver's own payout script, as a quote reports it.</param>
    /// <param name="sats">How much spendable float the corridor needs.</param>
    /// <param name="ct">Cancels the top-up.</param>
    /// <returns>True if the solver ends up funded.</returns>
    /// <remarks>
    /// <para>
    /// The Lightning corridors need this for the receive direction only, and only because the solver
    /// pays out first there: it funds the Arkade lockup before the invoice it is owed has settled, so
    /// a solver whose float is bound up in coins it is still settling refuses with
    /// <c>insufficient_unreserved_balance</c> and the swap simply never appears. Waiting it out is not
    /// a fix — the float frees on the operator's settlement schedule, not the test's.
    /// </para>
    /// <para>
    /// Call it after the quote and before paying the invoice. That window is the right one: the quote
    /// is what reveals the solver's own script, and the solver does not fund until the payment lands,
    /// so topping up in between is early enough to matter and late enough to be possible.
    /// </para>
    /// </remarks>
    internal static async Task<bool> EnsureBtcFloat(
        ECXOnlyPubKey serverKey, string solverPkScriptHex, ulong sats, CancellationToken ct = default)
    {
        // Check before funding a wallet to pay with: standing one up costs an on-chain fund and a
        // settle, and a solver that is already liquid needs neither.
        var probe = await FundedWalletHelper.GetFundedWallet(vtxoCount: 1, amountSatsPerVtxo: 1_000);
        if (await HasSpendableSats(probe.clientTransport, solverPkScriptHex, sats, ct)) return true;

        var funder = await FundedWalletHelper.GetFundedWallet(
            vtxoCount: 1, amountSatsPerVtxo: checked((int)(sats + 500_000)));

        var solverAddress = ArkAddress.FromScriptPubKey(
            new NBitcoin.Script(Convert.FromHexString(solverPkScriptHex)), serverKey);

        var coinService = new CoinService(funder.clientTransport, funder.contracts,
            [new NArk.Core.Transformers.PaymentContractTransformer(funder.walletProvider)]);
        var spending = new SpendingService(
            funder.vtxoStorage, funder.contracts, funder.walletProvider, coinService, funder.contractService,
            funder.clientTransport, new DefaultCoinSelector(), funder.safetyService,
            TestPersistance.TestStorage.CreateIntentStorage());

        await spending.Spend(funder.walletIdentifier,
            [new ArkTxOut(ArkTxOutType.Vtxo, NBitcoin.Money.Satoshis(sats), solverAddress)], ct);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await HasSpendableSats(funder.clientTransport, solverPkScriptHex, sats, ct)) return true;
            await Task.Delay(1000, ct);
        }
        return false;
    }

    /// <summary>True once the solver's address holds an unspent, unswept VTXO of at least <paramref name="sats"/>.</summary>
    private static async Task<bool> HasSpendableSats(
        IClientTransport transport, string scriptHex, ulong sats, CancellationToken ct)
    {
        await foreach (var v in transport.GetVtxoByScriptsAsSnapshot(
                           new HashSet<string> { scriptHex }, cancellationToken: ct))
        {
            if (!string.IsNullOrEmpty(v.SpentByTransactionId) || v.Swept) continue;
            if (v.Amount >= sats) return true;
        }
        return false;
    }

    /// <summary>Register one market, both directions, the way <c>solver-init</c> does.</summary>
    /// <remarks>
    /// <c>POST /v1/market</c>, not the <c>POST /v1/pair</c> this helper spoke until solverd v0.0.6 —
    /// that route is gone and answers 404, which is what a stale helper looks like from the outside.
    /// The shape is mirrored from <c>regtest/docker/solver-init/index.mjs</c> deliberately: it is the
    /// reference bootstrap for the same daemon, so a drift there is a drift the next image bump
    /// should surface here too.
    /// <para>
    /// <paramref name="pricePath"/> is a JSON pointer into the feed's response, and solverd has
    /// required it since v0.0.3 for any feed that is not a Binance or CoinGecko URL — the mock feed
    /// is neither.
    /// </para>
    /// </remarks>
    private static async Task RegisterMarket(
        HttpClient http, string baseAsset, string quoteAsset, string priceFeed, string pricePath,
        CancellationToken ct)
    {
        var body = new
        {
            market = new
            {
                base_asset = baseAsset,
                quote_asset = quoteAsset,
                min_quote_amount = 1,
                max_quote_amount = 100_000_000,
                min_base_amount = 1,
                max_base_amount = 100_000_000,
                price_feed = priceFeed,
                price_path = pricePath,
            },
        };

        var resp = await http.PostAsJsonAsync("v1/market", body, ct);
        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Conflict)
            return;

        // A re-registered market is fine; only a genuine server error should fail the helper.
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!text.Contains("exist", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"POST /v1/market '{baseAsset}/{quoteAsset}' failed ({(int)resp.StatusCode}): {text}");
    }
}
