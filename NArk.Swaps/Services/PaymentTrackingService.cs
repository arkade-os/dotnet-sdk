using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Payments;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;

namespace NArk.Swaps.Services;

/// <summary>
/// Hosted service that subscribes to protocol events (VTXOs, intents, swaps) and
/// automatically updates payment and payment request statuses.
/// Registered via <see cref="NArk.Storage.EfCore.Hosting.StorageServiceCollectionExtensions.AddArkPaymentTracking"/>.
/// </summary>
public class PaymentTrackingService(
    IPaymentStorage paymentStorage,
    IPaymentRequestStorage paymentRequestStorage,
    IVtxoStorage vtxoStorage,
    IIntentStorage intentStorage,
    ISwapStorage swapStorage,
    ILogger<PaymentTrackingService> logger) : IHostedService, IDisposable
{
    // Serializes VTXO processing to prevent race conditions when multiple VTXOs
    // arrive for the same payment request in the same batch round.
    private readonly SemaphoreSlim _vtxoLock = new(1, 1);
    private bool _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged += OnVtxoChanged;
        intentStorage.IntentChanged += OnIntentChanged;
        swapStorage.SwapsChanged += OnSwapChanged;
        logger.LogInformation("PaymentTrackingService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        logger.LogInformation("PaymentTrackingService stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// When a VTXO arrives, check if it matches a pending payment request.
    /// Serialized via <see cref="_vtxoLock"/> to prevent concurrent read-compute-write
    /// races when multiple VTXOs target the same payment request.
    /// </summary>
    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            if (vtxo.IsSpent()) return;

            await _vtxoLock.WaitAsync();
            try
            {
                var request = await paymentRequestStorage.GetPaymentRequestByScript(vtxo.Script);
                if (request is null) return;

                var newReceived = request.ReceivedAmount + vtxo.Amount;
                var (newStatus, overpayment) = ResolveRequestStatus(request, newReceived);

                var receivedAssets = MergeAssets(request.ReceivedAssets, vtxo.Assets);

                await paymentRequestStorage.UpdatePaymentRequestStatus(
                    request.WalletId, request.RequestId, newStatus, newReceived, overpayment, receivedAssets);

                logger.LogInformation(
                    "Payment request {RequestId} received {Amount} sats (total: {Total}), status: {Status}",
                    request.RequestId, vtxo.Amount, newReceived, newStatus);
            }
            finally
            {
                _vtxoLock.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing VTXO {TxId}:{Index} for payment request",
                vtxo.TransactionId, vtxo.TransactionOutputIndex);
        }
    }

    /// <summary>
    /// Determines the new status and overpayment of a payment request based on received amount.
    /// Any-amount requests (Amount=null) are Paid immediately on first funds.
    /// Fixed-amount requests require exact or over — no underpayment tolerance.
    /// </summary>
    private static (ArkPaymentRequestStatus Status, ulong Overpayment) ResolveRequestStatus(
        ArkPaymentRequest request, ulong newReceived)
    {
        if (request.Amount is null)
            return (ArkPaymentRequestStatus.Paid, 0);

        var target = request.Amount.Value;

        if (newReceived >= target)
            return (ArkPaymentRequestStatus.Paid, newReceived - target);

        return (ArkPaymentRequestStatus.PartiallyPaid, 0);
    }

    /// <summary>
    /// Merges newly received assets into an existing list, summing amounts for the same AssetId.
    /// Handles duplicate AssetIds in existing list defensively.
    /// </summary>
    internal static IReadOnlyList<VtxoAsset>? MergeAssets(
        IReadOnlyList<VtxoAsset>? existing, IReadOnlyList<VtxoAsset>? incoming)
    {
        if (incoming is null or { Count: 0 }) return existing;
        if (existing is null or { Count: 0 }) return incoming;

        var merged = new Dictionary<string, ulong>();
        foreach (var asset in existing)
            merged[asset.AssetId] = merged.GetValueOrDefault(asset.AssetId) + asset.Amount;
        foreach (var asset in incoming)
            merged[asset.AssetId] = merged.GetValueOrDefault(asset.AssetId) + asset.Amount;

        return merged.Select(kv => new VtxoAsset(kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// When an intent state changes, update linked outbound payments.
    /// </summary>
    private async void OnIntentChanged(object? sender, ArkIntent intent)
    {
        try
        {
            var payments = await paymentStorage.GetPayments(
                intentTxIds: [intent.IntentTxId]);

            foreach (var payment in payments)
            {
                if (payment.Status != ArkPaymentStatus.Pending) continue;

                var newStatus = intent.State switch
                {
                    ArkIntentState.BatchSucceeded => ArkPaymentStatus.Completed,
                    ArkIntentState.BatchFailed => ArkPaymentStatus.Failed,
                    ArkIntentState.Cancelled => ArkPaymentStatus.Cancelled,
                    _ => ArkPaymentStatus.Pending
                };

                if (newStatus == ArkPaymentStatus.Pending) continue;

                var failReason = newStatus is ArkPaymentStatus.Failed or ArkPaymentStatus.Cancelled
                    ? intent.CancellationReason ?? (newStatus == ArkPaymentStatus.Cancelled ? "Intent cancelled" : "Intent failed")
                    : null;

                await paymentStorage.UpdatePaymentStatus(
                    payment.WalletId, payment.PaymentId, newStatus, failReason);

                logger.LogInformation(
                    "Payment {PaymentId} updated to {Status} from intent {IntentTxId}",
                    payment.PaymentId, newStatus, intent.IntentTxId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing intent {IntentTxId} for payment tracking",
                intent.IntentTxId);
        }
    }

    /// <summary>
    /// When a swap state changes, update linked outbound payments.
    /// </summary>
    private async void OnSwapChanged(object? sender, ArkSwap swap)
    {
        try
        {
            var payments = await paymentStorage.GetPayments(swapIds: [swap.SwapId]);

            foreach (var payment in payments)
            {
                if (payment.Status != ArkPaymentStatus.Pending) continue;

                var newStatus = swap.Status switch
                {
                    ArkSwapStatus.Settled => ArkPaymentStatus.Completed,
                    ArkSwapStatus.Failed => ArkPaymentStatus.Failed,
                    ArkSwapStatus.Refunded => ArkPaymentStatus.Failed,
                    _ => ArkPaymentStatus.Pending
                };

                if (newStatus == ArkPaymentStatus.Pending) continue;

                var failReason = newStatus == ArkPaymentStatus.Failed
                    ? swap.FailReason ?? $"Swap {swap.Status}"
                    : null;

                await paymentStorage.UpdatePaymentStatus(
                    payment.WalletId, payment.PaymentId, newStatus, failReason);

                logger.LogInformation(
                    "Payment {PaymentId} updated to {Status} from swap {SwapId}",
                    payment.PaymentId, newStatus, swap.SwapId);
            }

            if (swap.Status == ArkSwapStatus.Settled &&
                swap.SwapType == ArkSwapType.ReverseSubmarine)
            {
                await HandleReverseSwapSettled(swap);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing swap {SwapId} for payment tracking", swap.SwapId);
        }
    }

    /// <summary>
    /// Reverse submarine swaps settle Lightning → Ark. Asset tracking is not applicable here
    /// because Lightning invoices are BTC-only; the VTXO that arrives will be tracked separately
    /// via <see cref="OnVtxoChanged"/> which handles assets.
    /// </summary>
    private async Task HandleReverseSwapSettled(ArkSwap swap)
    {
        var requests = await paymentRequestStorage.GetPaymentRequests(
            walletIds: [swap.WalletId],
            statuses: [ArkPaymentRequestStatus.Pending, ArkPaymentRequestStatus.PartiallyPaid]);

        foreach (var request in requests)
        {
            if (request.SwapId != swap.SwapId) continue;

            var receivedAmount = request.ReceivedAmount + (ulong)swap.ExpectedAmount;
            var (newStatus, overpayment) = ResolveRequestStatus(request, receivedAmount);

            await paymentRequestStorage.UpdatePaymentRequestStatus(
                request.WalletId, request.RequestId, newStatus, receivedAmount, overpayment);
            break;
        }
    }

    private void Unsubscribe()
    {
        vtxoStorage.VtxosChanged -= OnVtxoChanged;
        intentStorage.IntentChanged -= OnIntentChanged;
        swapStorage.SwapsChanged -= OnSwapChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unsubscribe();
        _vtxoLock.Dispose();
    }
}
