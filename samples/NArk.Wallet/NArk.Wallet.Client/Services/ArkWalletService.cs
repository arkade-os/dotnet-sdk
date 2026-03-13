using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Swaps.Abstractions;
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
    ISwapStorage swapStorage,
    IAssetManager assetManager)
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

    public async Task<(string ArkAddress, string BoardingAddress)> GetReceiveInfo(string walletId)
    {
        var addressProvider = await walletProvider.GetAddressProviderAsync(walletId)
            ?? throw new InvalidOperationException("Wallet not found");

        var serverInfo = await transport.GetServerInfoAsync();

        var (contract, _) = await addressProvider.GetNextContract(
            NextContractPurpose.Receive, ContractActivityState.Active);
        var arkAddress = contract.GetArkAddress().ToString(serverInfo.Network == Network.Main);

        var (boardingContract, _) = await addressProvider.GetNextContract(
            NextContractPurpose.Boarding, ContractActivityState.Active);
        var boardingAddress = boardingContract.GetScriptPubKey()
            .GetDestinationAddress(serverInfo.Network)?.ToString() ?? "";

        return (arkAddress, boardingAddress);
    }

    // ── Swaps ──

    public async Task<IReadOnlyCollection<NArk.Swaps.Models.ArkSwap>> GetSwaps(string walletId)
        => await swapStorage.GetSwaps(walletIds: [walletId]);

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

    // ── Helpers ──

    private static string GenerateNsec()
    {
        var key = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var keyBytes = new byte[32];
        key.WriteToSpan(keyBytes);
        var encoder = NBitcoin.DataEncoders.Encoders.Bech32("nsec");
        return encoder.EncodeData(keyBytes, NBitcoin.DataEncoders.Bech32EncodingType.BECH32);
    }
}
