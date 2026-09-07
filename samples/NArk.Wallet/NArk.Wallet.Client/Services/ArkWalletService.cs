using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Assets;
using NArk.Core.Recovery;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Hosting;
using NBitcoin;

using NArk.ArkadeIntents.Assets;
namespace NArk.Wallet.Client.Services;

/// <summary>
/// Client-side wallet service that calls SDK services directly (no backend API).
/// Replaces ArkadeApiClient for the pure-WASM architecture.
/// </summary>
public class ArkWalletService(
    IWalletStorage walletStorage,
    IWalletProvider walletProvider,
    IClientTransport transport,
    ISpendingService spendingService,
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IIntentStorage intentStorage,
    IAssetManager assetManager,
    IOnchainService onchainService,
    IContractService contractService,
    HdWalletRecoveryService recoveryService,
    PendingArkTransactionRecoveryService pendingTxRecoveryService,
    ArkNetworkConfig networkConfig,
    NArk.ArkadeIntents.Assets.AssetIntentsManager arkadeSwaps,
    NArk.ArkadeIntents.Services.SolverDiscoveryService solverDiscovery,
    NArk.ArkadeIntents.IArkadeIntentStorage arkadeIntentStorage,
    ArkadeLightningService arkadeLightning)
{
    // ── Wallets ──

    public async Task<IReadOnlySet<ArkWalletInfo>> GetWallets()
        => await walletStorage.LoadAllWallets();

    /// <summary>
    /// Creates a wallet and persists it in local storage.
    /// </summary>
    /// <param name="secret">
    /// An existing secret to import — a BIP39 mnemonic (HD wallet) or an <c>nsec1…</c>
    /// (legacy single-key wallet). When <c>null</c>, a fresh 12-word BIP39 mnemonic is
    /// generated and the wallet is created as HD, so every receive gives a new address
    /// and the wallet can be recovered by gap-limit scan from the phrase alone.
    /// </param>
    public async Task<ArkWalletInfo> CreateWallet(string? secret = null)
    {
        var serverInfo = await transport.GetServerInfoAsync();
        var walletSecret = secret ?? GenerateMnemonic();
        var wallet = await WalletFactory.CreateWallet(walletSecret, null, serverInfo);
        await walletStorage.SaveWallet(wallet);
        return wallet;
    }

    /// <summary>
    /// Validates an imported secret before it reaches <see cref="CreateWallet"/>, so the UI
    /// can show a precise message instead of a raw parser exception. Accepts a BIP39
    /// mnemonic in the English wordlist, or an <c>nsec1…</c> single key.
    /// </summary>
    /// <returns><c>null</c> when the secret is valid, otherwise the reason it was rejected.</returns>
    public static string? ValidateSecret(string secret)
    {
        secret = secret.Trim();
        if (string.IsNullOrEmpty(secret))
        {
            return "Enter a recovery phrase or nsec.";
        }

        if (secret.StartsWith("nsec", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            _ = new Mnemonic(secret, Wordlist.English);
            return null;
        }
        catch
        {
            return "Not a valid BIP39 recovery phrase (expected 12 or 24 English words) or nsec1… key.";
        }
    }

    /// <summary>
    /// Run an HD-wallet gap-limit recovery scan for a freshly imported wallet.
    /// Discovers contracts that were used by a previous instance of the same
    /// mnemonic — VTXOs (arkd indexer) and boarding UTXOs (on-chain) — and
    /// persists them in local storage so balances and history reflect the
    /// wallet's prior activity.
    ///
    /// Swaps are not among what comes back. The provider that held its own
    /// record of them went with the swaps package; an intent swap is recorded
    /// locally against a covenant this wallet derived, so recovering one means
    /// rebuilding it from the chain rather than asking a counterparty.
    /// </summary>
    /// <remarks>
    /// Only meaningful for HD wallets; SingleKey wallets have no derivation index
    /// and the scanner will throw. Safe to re-run; subsequent calls are
    /// idempotent (deduped by script, never lowers <c>LastUsedIndex</c>).
    /// </remarks>
    public Task<RecoveryReport> RestoreWallet(string walletId, RecoveryOptions? options = null,
        CancellationToken cancellationToken = default)
        => recoveryService.ScanAsync(walletId, options, cancellationToken);

    /// <summary>
    /// Reconciles Arkade transactions that the server has registered as pending — i.e.
    /// the SDK called <see cref="IClientTransport.SubmitTx"/> but the matching
    /// <see cref="IClientTransport.FinalizeTx"/> never followed (process crash, network
    /// drop, app close). Without recovery those coins are stuck because the server
    /// only allows the original pending tx to be finalized.
    /// </summary>
    /// <remarks>
    /// Startup recovery already runs automatically via <c>ArkHostedLifecycle</c>; call
    /// this when you want deterministic timing — e.g. immediately after a user unlock
    /// or restored backup, before showing the balance screen. Returns the arkTxIds
    /// that were finalized; per-tx failures are absorbed and surfaced via
    /// <c>PendingArkTransactionRecoveryService.RecoveryFailed</c>.
    /// </remarks>
    public Task<IReadOnlyList<string>> FinalizePendingArkTransactions(string walletId,
        CancellationToken cancellationToken = default)
        => pendingTxRecoveryService.FinalizePendingArkTransactionsAsync(walletId, cancellationToken);

    public async Task DeleteWallet(string walletId)
        => await walletStorage.DeleteWallet(walletId);

    // ── Balance & VTXOs ──

    /// <summary>Spendable balance, in sats.</summary>
    /// <remarks>
    /// Failures are not swallowed. This used to return 0 on any exception, which reports "I could
    /// not work out what you have" as "you have nothing" — the one answer a balance must never give
    /// wrongly, because every decision the user makes next is based on it. A wallet that shows an
    /// error is worse to look at and better to trust.
    /// </remarks>
    public async Task<long> GetBalance(string walletId)
    {
        var coins = await spendingService.GetAvailableCoins(walletId);
        return coins.Sum(c => c.Amount.Satoshi);
    }

    public async Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(string walletId, int skip = 0, int take = 50)
        => await vtxoStorage.GetVtxos(walletIds: [walletId], skip: skip, take: take);

    // ── Spending ──

    public async Task<string> Send(string walletId, string destinationAddress, long amountSats)
    {
        var dest = ArkAddress.Parse(destinationAddress);
        var output = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(amountSats), dest);
        var txId = await spendingService.Spend(walletId, [output]);
        return txId.ToString();
    }

    // ── Receive ──

    public record ReceiveInfo(
        string ArkAddress, string BoardingAddress,
        string ArkContractScript, string BoardingContractScript);

    public async Task<ReceiveInfo> GetReceiveInfo(string walletId)
    {
        var serverInfo = await transport.GetServerInfoAsync();

        // Use IContractService.DeriveContract to persist contracts (not raw addressProvider)
        var arkContract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive);
        var arkAddress = arkContract.GetArkAddress().ToString(serverInfo.Network == Network.Main);
        var arkScript = arkContract.GetScriptPubKey().ToHex();

        var boardingContract = await contractService.DeriveContract(walletId, NextContractPurpose.Boarding);
        var boardingAddress = boardingContract.GetScriptPubKey()
            .GetDestinationAddress(serverInfo.Network)?.ToString() ?? "";
        var boardingScript = boardingContract.GetScriptPubKey().ToHex();

        return new ReceiveInfo(arkAddress, boardingAddress, arkScript, boardingScript);
    }

    /// <summary>
    /// Mints an invoice whose payment arrives as Arkade sats (<c>lightning:BTC→arkade:BTC</c>).
    /// Returns the invoice to hand to the payer.
    /// </summary>
    /// <remarks>
    /// The solver funds the covenant before it is paid anything, and only our claim — which
    /// publishes the preimage — lets it settle the payment it is holding. Claiming is this wallet's
    /// job; a covclaimd, if one is configured, only races it so the claim still happens while the
    /// wallet is closed.
    /// </remarks>
    public async Task<string> ReceiveOverLightning(string walletId, long amountSats)
    {
        var pending = await arkadeLightning.CreateInvoiceAsync(walletId, amountSats);
        return pending.Invoice;
    }

    /// <summary>
    /// Whether a solver serving a Lightning corridor was found on the registry for this network.
    /// </summary>
    /// <remarks>
    /// Asked rather than configured: no counterparty is named in this build, so the answer is a
    /// registry lookup and can legitimately be "none today".
    /// </remarks>
    public Task<bool> LightningAvailable(CancellationToken ct = default) =>
        arkadeLightning.IsAvailableAsync(ct);

    /// <summary>
    /// Whether a funded receive swap will still be claimed if this wallet is closed — i.e. whether
    /// a covclaimd is configured. Receiving works either way; without one the claim is ours to make
    /// inside the window.
    /// </summary>
    public bool LightningOfflineClaimCover => arkadeLightning.HasOfflineClaimCover;

    /// <summary>This wallet's Lightning swaps, newest first.</summary>
    public Task<IReadOnlyList<NArk.ArkadeIntents.Models.ArkadeSwapIntent>> GetLightningSwaps(
        string walletId, CancellationToken ct = default)
        => arkadeLightning.ListAsync(walletId, ct);

    /// <summary>Take delivery of a funded receive swap now.</summary>
    public Task<NArk.ArkadeIntents.Models.ArkadeSwapIntent> ClaimLightningSwap(
        string swapId, CancellationToken ct = default)
        => arkadeLightning.ClaimAsync(swapId, ct);

    /// <summary>Take back the deposit on a send swap the solver never filled.</summary>
    public Task<NArk.ArkadeIntents.Models.ArkadeSwapIntent> RefundLightningSwap(
        string swapId, CancellationToken ct = default)
        => arkadeLightning.RefundAsync(swapId, ct);

    // ── Arkade asset swaps (covenant + solver market) ──

    /// <summary>The solver-registry network name for the configured Ark network.</summary>
    public string SwapNetworkName =>
        networkConfig == ArkNetworkConfig.Mainnet ? "bitcoin" : "mutinynet";

    /// <summary>Discover the tradable BTC⇄asset markets published by solvers on this network.</summary>
    public Task<IReadOnlyList<NArk.ArkadeIntents.SolverRegistry.IndexedMarket>> GetSwapMarkets(
        CancellationToken ct = default)
        => solverDiscovery.DiscoverMarketsAsync(SwapNetworkName, cancellationToken: ct);

    /// <summary>Current normalized price for a market (quote units per base unit).</summary>
    public Task<decimal> GetMarketPrice(
        NArk.ArkadeIntents.SolverRegistry.SolverMarket market, CancellationToken ct = default)
        => solverDiscovery.FetchPriceAsync(market, ct);

    /// <summary>This wallet's Arkade swap intents (pending / cancelling / cancelled).</summary>
    public Task<IReadOnlyCollection<NArk.ArkadeIntents.Models.ArkadeSwapIntent>> GetAssetSwaps(
        string walletId, CancellationToken ct = default)
        => arkadeIntentStorage.GetArkadeSwapIntents(walletIds: [walletId], cancellationToken: ct);

    /// <summary>
    /// Create a BTC→asset swap: fund a covenant offer with <paramref name="depositSats"/> BTC and
    /// ask for <paramref name="wantAssetAmount"/> atomic units of the market's quote asset. A solver
    /// on the market fulfils it (pays the asset to the wallet's payout address).
    /// </summary>
    public Task<NArk.ArkadeIntents.Models.ArkadeSwapIntent> CreateBtcToAssetSwap(
        string walletId, NArk.ArkadeIntents.SolverRegistry.IndexedMarket market,
        long depositSats, long wantAssetAmount, CancellationToken ct = default)
        => arkadeSwaps.CreateSwap(new NArk.ArkadeIntents.Assets.CreateSwapRequest(
            walletId, NArk.ArkadeIntents.Models.ArkadeSwapIntentType.BtcToAsset,
            depositSats, wantAssetAmount, AssetId.FromString(market.QuoteAsset.Id)), ct);

    /// <summary>Cancel a pending swap and reclaim the deposit via the covenant's cancel path.</summary>
    public Task<NArk.ArkadeIntents.Models.ArkadeSwapIntent> CancelAssetSwap(
        string swapId, CancellationToken ct = default)
        => arkadeSwaps.CancelSwap(swapId, ct);

    // ── Wallet Info ──

    /// <summary>
    /// Gets wallet details including the public key for display in settings.
    /// </summary>
    public async Task<ArkWalletInfo?> GetWalletInfo(string walletId)
    {
        var wallets = await walletStorage.LoadAllWallets();
        return wallets.FirstOrDefault(w => w.Id == walletId);
    }

    // ── Intents ──

    public async Task<IReadOnlyCollection<ArkIntent>> GetIntents(
        string walletId, ArkIntentState[]? states = null, int take = 50)
        => await intentStorage.GetIntents(walletIds: [walletId], states: states, take: take);

    // ── Contracts ──

    public async Task<IReadOnlyCollection<ArkContractEntity>> GetContracts(
        string walletId, bool? isActive = null, int take = 50)
        => await contractStorage.GetContracts(walletIds: [walletId], isActive: isActive, take: take);

    // ── Assets ──

    public async Task<(string TxId, string AssetId)> IssueAsset(
        string walletId, ulong amount, string? controlAssetId, Dictionary<string, string>? metadata)
    {
        var result = await assetManager.IssueAsync(walletId, new IssuanceParams(amount, controlAssetId, metadata));
        return (result.ArkTxId, result.AssetId);
    }

    public async Task<string> BurnAsset(string walletId, string assetId, ulong amount)
    {
        var txId = await assetManager.BurnAsync(walletId, new BurnParams(assetId, amount));
        return txId;
    }

    // ── Server Info ──

    public async Task<ArkServerInfo> GetServerInfo()
        => await transport.GetServerInfoAsync();

    // ── Collaborative Exit (on-chain send) ──

    public async Task<string> CollaborativeExit(string walletId, string btcAddress, long amountSats)
    {
        var serverInfo = await transport.GetServerInfoAsync();
        var addr = BitcoinAddress.Create(btcAddress, serverInfo.Network);
        var output = new ArkTxOut(ArkTxOutType.Onchain, Money.Satoshis(amountSats), addr);
        return await onchainService.InitiateCollaborativeExit(walletId, output);
    }

    // ── Lightning send (arkade:BTC → lightning:BTC) ──

    /// <summary>
    /// Pays a BOLT11 out of the Arkade balance. Returns the funding txid of the covenant the
    /// solver must reveal a preimage to claim.
    /// </summary>
    /// <remarks>
    /// The txid means the sats are locked, not that the invoice is paid. Only the preimage proves
    /// that, and it appears when the solver claims — until then the swap is still in flight and,
    /// past its refund locktime, still refundable.
    /// </remarks>
    public async Task<string> PayLightningInvoice(string walletId, string bolt11Invoice)
    {
        var funded = await arkadeLightning.PayInvoiceAsync(walletId, bolt11Invoice);
        return funded.FundingTxid;
    }

    // ── Network Config ──

    public ArkNetworkConfig GetNetworkConfig() => networkConfig;

    // ── Helpers ──

    private static string GenerateMnemonic()
        => new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
}
