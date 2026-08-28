using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Settlement;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Settlement;

/// <summary>
/// The asset rail: moves a balance of one Arkade-issued asset to an Arkade address, or
/// consolidates it onto a freshly derived address of the settling wallet when the
/// destination carries no address.
/// <para>
/// It handles same-asset transfers only — what leaves the wallet is what arrives. Settling
/// an asset into a <em>different</em> asset (USDT0 into BTC, USDT0 into another stablecoin)
/// is a conversion: register a rail whose <see cref="CanSettle"/> accepts that destination
/// and read <see cref="SettlementRequest.SourceAsset"/> for what it is being handed.
/// </para>
/// <para>
/// An asset VTXO carries a dust-sized satoshi amount alongside the asset. This rail keeps
/// that carrier intact: it funds one dust output per asset output, topping up from the
/// wallet's BTC coins when the spent carriers alone do not cover them, and returns the
/// asset remainder to the wallet as an asset change output rather than burning it.
/// </para>
/// <para>
/// A wallet's auto-sweep destination does not apply to the asset change. That setting rewrites
/// every send-to-self into the wallet's configured consolidation address, and the remainder of a
/// capped settlement is precisely the part the rule chose <em>not</em> to move — it has to stay
/// where the next settlement can reach it.
/// </para>
/// </summary>
public class ArkAssetSettlementService(
    ISpendingService spendingService,
    IContractService contractService,
    IClientTransport transport,
    ILogger<ArkAssetSettlementService>? logger = null) : ISettlementService
{
    /// <inheritdoc />
    public bool Available => true;

    /// <inheritdoc />
    public string? UnavailableReason => null;

    /// <summary>
    /// Accepts Arkade destinations whose asset is anything other than BTC — the asset id
    /// itself, as produced by <see cref="SettlementDestination.ArkAsset"/>.
    /// </summary>
    public bool CanSettle(SettlementDestination destination) =>
        destination.IsNetwork(SettlementNetworks.Ark)
        && !destination.IsAsset(SettlementAssets.Btc)
        && !string.IsNullOrWhiteSpace(destination.Asset);

    /// <inheritdoc />
    public async Task<SettlementResult> SettleAsync(
        SettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Amount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(request), request.Amount, "Settlement amount must be positive.");

        if (!CanSettle(request.Destination))
            throw new SettlementNotSupportedException(request.Destination,
                $"Destination {request.Destination.Network}/{request.Destination.Asset} is not handled by the Arkade asset rail.");

        // This rail transfers, it does not convert: the asset leaving the wallet has to be
        // the asset the destination expects.
        if (!request.SourceAsset.Equals(request.Destination.Asset, StringComparison.OrdinalIgnoreCase))
            throw new SettlementNotSupportedException(request.Destination,
                $"The Arkade asset rail cannot convert {request.SourceAsset} into {request.Destination.Asset}; register a rail for that conversion.");

        using var _walletScope = logger?.BeginScope(("WalletId", request.WalletId));

        var assetId = request.SourceAsset;
        var amount = (ulong)request.Amount;
        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);

        var candidates = request.Coins is { Count: > 0 } pinned
            ? pinned
            : await spendingService.GetAvailableCoins(request.WalletId, cancellationToken);

        var (assetInputs, gathered) = SelectAssetCoins(candidates, assetId, amount);
        if (gathered < amount)
            throw new InvalidOperationException(
                $"Wallet {request.WalletId} holds {gathered} of asset {assetId}, need {amount} to settle.");

        // Receive rather than SendToSelf, here and for the change below: on a wallet with an
        // auto-sweep destination the latter resolves to that address, and an asset settled to
        // self would leave the wallet instead of landing on it.
        var destination = request.Destination.Address is { } address
            ? ArkAddress.Parse(address)
            : (await contractService.DeriveContract(
                request.WalletId, NextContractPurpose.Receive, cancellationToken: cancellationToken))
            .GetArkAddress();

        var outputs = new List<ArkTxOut>
        {
            new(ArkTxOutType.Vtxo, serverInfo.Dust, destination)
            {
                Assets = [new ArkTxOutAsset(assetId, amount)]
            }
        };

        // The surplus the selected carriers hold beyond the settled amount has to come back
        // as an asset output of its own; without it the asset packet would show more asset
        // going in than coming out, which destroys the remainder.
        var remainder = gathered - amount;
        if (remainder > 0)
        {
            // The remainder is what a MaxAmount cap chose to keep for the next settlement, so it
            // has to stay reachable by this wallet.
            var changeAddress = (await contractService.DeriveContract(
                    request.WalletId, NextContractPurpose.Receive,
                    cancellationToken: cancellationToken))
                .GetArkAddress();

            outputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, changeAddress)
            {
                Assets = [new ArkTxOutAsset(assetId, remainder)]
            });
        }

        var inputs = await FundCarriersAsync(
            request, assetInputs, serverInfo.Dust * outputs.Count, candidates, cancellationToken);

        var txId = await spendingService.Spend(request.WalletId, [.. inputs], [.. outputs], cancellationToken);

        logger?.LogInformation(
            "Settled {Amount} of asset {AssetId} from wallet {WalletId} to Arkade destination, txId {TxId}",
            amount, assetId, request.WalletId, txId);

        // The asset itself moves whole; the only satoshis spent are the carriers, and any
        // surplus BTC on the selected coins returns to the wallet as change.
        return new SettlementResult(
            txId.ToString(),
            request.Amount,
            serverInfo.Dust.Satoshi,
            0,
            txId,
            request.Amount);
    }

    // Earliest-expiring carriers first, so settling an asset also drains the coins that
    // would otherwise need a sweep the soonest.
    private static (List<ArkCoin> Coins, ulong Gathered) SelectAssetCoins(
        IEnumerable<ArkCoin> candidates,
        string assetId,
        ulong target)
    {
        var selected = new List<ArkCoin>();
        ulong gathered = 0;

        var carriers = candidates
            .Where(coin => !coin.Unrolled)
            .Where(coin => AmountOf(coin, assetId) > 0)
            .OrderBy(coin => coin.GetRawExpiry() == 0 ? double.MaxValue : coin.GetRawExpiry());

        foreach (var coin in carriers)
        {
            if (gathered >= target)
                break;

            selected.Add(coin);
            gathered += AmountOf(coin, assetId);
        }

        return (selected, gathered);
    }

    // The dust outputs need funding: the carriers being spent cover part of it, and BTC-only
    // coins cover the rest — an asset send that splits one carrier into two outputs needs
    // more satoshis than it spends.
    private async Task<List<ArkCoin>> FundCarriersAsync(
        SettlementRequest request,
        List<ArkCoin> assetInputs,
        Money requiredSats,
        IEnumerable<ArkCoin> candidates,
        CancellationToken cancellationToken)
    {
        var inputs = new List<ArkCoin>(assetInputs);
        var provided = assetInputs.Aggregate(Money.Zero, (sum, coin) => sum + coin.TxOut.Value);
        if (provided >= requiredSats)
            return inputs;

        // Pinned coins are the caller's decision on what may be spent; topping them up with
        // coins they did not pick would spend beyond the mandate.
        var funding = request.Coins is { Count: > 0 }
            ? candidates
            : await spendingService.GetAvailableCoins(request.WalletId, cancellationToken);

        var selected = new HashSet<OutPoint>(assetInputs.Select(coin => coin.Outpoint));

        foreach (var coin in funding
                     .Where(coin => !coin.Unrolled && coin.Assets is null or { Count: 0 })
                     .Where(coin => !selected.Contains(coin.Outpoint))
                     .OrderByDescending(coin => coin.TxOut.Value))
        {
            if (provided >= requiredSats)
                break;

            inputs.Add(coin);
            provided += coin.TxOut.Value;
        }

        if (provided < requiredSats)
            throw new InvalidOperationException(
                $"Wallet {request.WalletId} holds {provided} sats of spendable BTC, need {requiredSats} to carry the asset outputs.");

        return inputs;
    }

    private static ulong AmountOf(ArkCoin coin, string assetId) =>
        coin.Assets is not { Count: > 0 } assets
            ? 0
            : assets
                .Where(asset => asset.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase))
                .Aggregate(0UL, (sum, asset) => sum + asset.Amount);
}
