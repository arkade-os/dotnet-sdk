using BTCPayServer.Lightning;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Recovery;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Recovery;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Hosting;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Services;
using NBitcoin;
using NBitcoin.Secp256k1;

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
    ISwapStorage swapStorage,
    IIntentStorage intentStorage,
    IAssetManager assetManager,
    IOnchainService onchainService,
    IContractService contractService,
    SwapsManagementService swapsManagementService,
    BoltzLimitsValidator boltzLimitsValidator,
    HdWalletRecoveryService recoveryService,
    ArkNetworkConfig networkConfig)
{
    // ── Wallets ──

    public async Task<IReadOnlySet<ArkWalletInfo>> GetWallets()
        => await walletStorage.LoadAllWallets();

    public async Task<ArkWalletInfo> CreateWallet(string? secret = null)
    {
        var serverInfo = await transport.GetServerInfoAsync();
        var walletSecret = secret ?? GenerateNsec();
        var wallet = await WalletFactory.CreateWallet(walletSecret, null, serverInfo);
        await walletStorage.SaveWallet(wallet);
        return wallet;
    }

    /// <summary>
    /// Run an HD-wallet gap-limit recovery scan for a freshly imported wallet.
    /// Discovers contracts that were used by a previous instance of the same
    /// mnemonic — VTXOs (arkd indexer), boarding UTXOs (on-chain), and Boltz
    /// swaps — and persists them in local storage so balances and history
    /// reflect the wallet's prior activity.
    /// </summary>
    /// <remarks>
    /// Only meaningful for HD wallets; SingleKey wallets have no derivation index
    /// and the scanner will throw. Safe to re-run; subsequent calls are
    /// idempotent (deduped by script, never lowers <c>LastUsedIndex</c>).
    /// </remarks>
    public Task<RecoveryReport> RestoreWallet(string walletId, RecoveryOptions? options = null,
        CancellationToken cancellationToken = default)
        => recoveryService.ScanAsync(walletId, options, cancellationToken);

    public async Task DeleteWallet(string walletId)
        => await walletStorage.DeleteWallet(walletId);

    // ── Balance & VTXOs ──

    public async Task<long> GetBalance(string walletId)
    {
        try
        {
            var coins = await spendingService.GetAvailableCoins(walletId);
            return coins.Sum(c => c.Amount.Satoshi);
        }
        catch { return 0; }
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

    // ── Swaps ──

    public async Task<IReadOnlyCollection<NArk.Swaps.Models.ArkSwap>> GetSwaps(string walletId)
        => await swapStorage.GetSwaps(walletIds: [walletId]);

    /// <summary>
    /// Initiates a reverse submarine swap (Lightning → Ark). Returns the Lightning invoice to pay.
    /// </summary>
    public async Task<string> InitiateReverseSwap(string walletId, long amountSats)
    {
        var invoiceParams = new CreateInvoiceParams(
            LightMoney.Satoshis(amountSats),
            "Arkade Wallet Receive",
            TimeSpan.FromHours(1));
        return await swapsManagementService.InitiateReverseSwap(walletId, invoiceParams);
    }

    /// <summary>
    /// Initiates a BTC→ARK chain swap. Returns the BTC address to send to.
    /// </summary>
    public async Task<(string BtcAddress, string SwapId, long ExpectedSats)> InitiateChainSwap(
        string walletId, long amountSats)
        => await swapsManagementService.InitiateBtcToArkChainSwap(walletId, amountSats);

    /// <summary>
    /// Gets Boltz swap limits for all swap types.
    /// </summary>
    public async Task<BoltzAllLimits?> GetBoltzLimits()
        => await boltzLimitsValidator.GetAllLimitsAsync();

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

    // ── Submarine Swap (Ark → Lightning) ──

    public async Task<string> PayLightningInvoice(string walletId, string bolt11Invoice)
    {
        var serverInfo = await transport.GetServerInfoAsync();
        var invoice = BOLT11PaymentRequest.Parse(bolt11Invoice, serverInfo.Network);
        return await swapsManagementService.InitiateSubmarineSwap(walletId, invoice);
    }

    // ── Chain Swap (Ark → BTC on-chain via Boltz) ──

    public async Task<string> SendArkToBtcChainSwap(string walletId, long amountSats, string btcAddress)
    {
        var serverInfo = await transport.GetServerInfoAsync();
        var addr = BitcoinAddress.Create(btcAddress, serverInfo.Network);
        return await swapsManagementService.InitiateArkToBtcChainSwap(walletId, amountSats, addr);
    }

    // ── Network Config ──

    public ArkNetworkConfig GetNetworkConfig() => networkConfig;

    // ── Helpers ──

    private static string GenerateNsec()
    {
        var key = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var keyBytes = new byte[32];
        key.WriteToSpan(keyBytes);
        var encoder = NBitcoin.DataEncoders.Encoders.Bech32("nsec");
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        return encoder.EncodeData(keyBytes, NBitcoin.DataEncoders.Bech32EncodingType.BECH32);
    }
}
