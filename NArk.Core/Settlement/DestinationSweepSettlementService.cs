using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Settlement;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;

namespace NArk.Core.Settlement;

/// <summary>
/// The default settlement rail: sweeps a wallet's balance to a destination the SDK can
/// reach on its own.
/// <list type="bullet">
/// <item>An Arkade address — an off-chain send via <see cref="ISpendingService"/>.</item>
/// <item>The settling wallet itself (<see cref="SettlementDestination.ArkSelf"/>) — funds land on a freshly derived address.</item>
/// <item>An on-chain Bitcoin address — a collaborative exit, only when
/// <see cref="SettlementOptions.EnableCollaborativeExit"/> is set.</item>
/// </list>
/// <para>
/// Arkade-issued assets are not handled here: every amount on this rail is denominated in
/// satoshis. <see cref="ArkAssetSettlementService"/> moves an asset balance instead.
/// </para>
/// </summary>
public class DestinationSweepSettlementService(
    ISpendingService spendingService,
    IContractService contractService,
    IClientTransport transport,
    IOptions<SettlementOptions> options,
    IOnchainService? onchainService = null,
    ILogger<DestinationSweepSettlementService>? logger = null) : ISettlementService
{
    /// <inheritdoc />
    public bool Available => true;

    /// <inheritdoc />
    public string? UnavailableReason => null;

    /// <inheritdoc />
    public bool CanSettle(SettlementDestination destination)
    {
        if (!destination.IsAsset(SettlementAssets.Btc))
            return false;

        if (destination.IsNetwork(SettlementNetworks.Ark))
            return true;

        // Unlike an Arkade destination, which reads a missing address as "back to this wallet",
        // there is nothing to derive on-chain: without an address the exit would have nowhere
        // to pay, and BitcoinAddress.Create would throw well past the point of no return.
        return destination.IsNetwork(SettlementNetworks.Bitcoin)
               && !string.IsNullOrWhiteSpace(destination.Address)
               && options.Value.EnableCollaborativeExit
               && onchainService is not null;
    }

    /// <inheritdoc />
    public async Task<SettlementResult> SettleAsync(
        SettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Amount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(request), request.Amount, "Settlement amount must be positive.");

        // Amounts here are satoshis end to end, so an asset-denominated request would be
        // read as a satoshi amount and quietly move the wrong value.
        if (!request.IsBtc)
            throw new SettlementNotSupportedException(request.Destination,
                $"The destination sweep settles BTC only; source asset {request.SourceAsset} needs an asset rail.");

        if (!CanSettle(request.Destination))
            throw new SettlementNotSupportedException(request.Destination,
                $"Destination {request.Destination.Network}/{request.Destination.Asset} is not handled by the destination sweep.");

        using var _walletScope = logger?.BeginScope(("WalletId", request.WalletId));

        return request.Destination.IsNetwork(SettlementNetworks.Ark)
            ? await SettleOffchainAsync(request, cancellationToken)
            : await SettleOnchainAsync(request, cancellationToken);
    }

    private async Task<SettlementResult> SettleOffchainAsync(
        SettlementRequest request,
        CancellationToken cancellationToken)
    {
        var destination = request.Destination.Address is { } address
            ? ArkAddress.Parse(address)
            : (await contractService.DeriveContract(
                request.WalletId, NextContractPurpose.SendToSelf, cancellationToken: cancellationToken))
            .GetArkAddress();

        var output = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(request.Amount), destination);

        var txId = request.Coins is { Count: > 0 } coins
            ? await spendingService.Spend(request.WalletId, [.. coins], [output], cancellationToken)
            : await spendingService.Spend(request.WalletId, [output], cancellationToken);

        logger?.LogInformation(
            "Settled {AmountSats} sats from wallet {WalletId} to Arkade destination, txId {TxId}",
            request.Amount, request.WalletId, txId);

        // An Arkade off-chain send carries no fee at this layer: whatever the selected
        // coins exceed the output by returns to the wallet as change.
        return new SettlementResult(
            txId.ToString(),
            request.Amount,
            request.Amount,
            0,
            txId);
    }

    private async Task<SettlementResult> SettleOnchainAsync(
        SettlementRequest request,
        CancellationToken cancellationToken)
    {
        if (onchainService is null)
            throw new SettlementNotSupportedException(request.Destination,
                "Collaborative exit settlement requires an IOnchainService registration.");

        if (request.Coins is { Count: > 0 })
            throw new NotSupportedException(
                "Collaborative exit settlement performs its own coin selection and fee estimation; " +
                "SettlementRequest.Coins cannot be honoured on this rail.");

        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);
        var destination = BitcoinAddress.Create(request.Destination.Address!, serverInfo.Network);
        var output = new ArkTxOut(ArkTxOutType.Onchain, Money.Satoshis(request.Amount), destination);

        var intentId = await onchainService.InitiateCollaborativeExit(request.WalletId, output, cancellationToken);

        logger?.LogInformation(
            "Settled {AmountSats} sats from wallet {WalletId} to on-chain address {Address} via collaborative exit, intent {IntentId}",
            request.Amount, request.WalletId, destination, intentId);

        // The exit spends more than the requested amount to cover the batch fee, and the exact
        // figure only exists once the batch confirms — hence null rather than a zero that would
        // read as "free".
        return new SettlementResult(
            intentId,
            request.Amount,
            request.Amount,
            null);
    }
}
