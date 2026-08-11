using Microsoft.Extensions.Logging;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq;

namespace NArk.ArkadeIntents.Services;

/// <summary>What <see cref="ArkadeIntentsService.AdvanceAsync"/> did about one swap.</summary>
/// <param name="SwapId">The swap.</param>
/// <param name="Action">What it called for.</param>
/// <param name="Acted">Whether the action actually ran.</param>
/// <param name="Txid">The transaction it produced, when it produced one.</param>
/// <param name="Error">Why it did not run, when it did not.</param>
public sealed record ArkadeIntentAdvance(
    string SwapId,
    ArkadeIntentAction Action,
    bool Acted,
    string? Txid = null,
    string? Error = null);

/// <summary>
/// One entry point for every kind of Arkade intent swap.
/// </summary>
/// <remarks>
/// <para>
/// The corridors are genuinely different underneath — an asset swap settles against an offer on the
/// stream, the Lightning legs negotiate by RFQ against a covenant — but they all end up as the same
/// <see cref="ArkadeSwapIntent"/> and are all watched by the same monitor. Callers should not have
/// to know which of three classes owns a given swap in order to list it, or to do the obvious thing
/// to it.
/// </para>
/// <para>
/// The part that is more than a facade is <see cref="AdvanceAsync"/>. The monitor already moves a
/// swap to <see cref="ArkadeSwapIntentStatus.Claimable"/> or
/// <see cref="ArkadeSwapIntentStatus.Refundable"/>, but until now nothing acted on that — the status
/// was a fact with no consequence, and the consequence is where the money is. This closes that loop
/// while refusing to guess: see <see cref="ArkadeIntentPolicy"/> for the line between something that
/// follows and something that is the caller's call.
/// </para>
/// </remarks>
public sealed class ArkadeIntentsService
{
    private readonly ArkadeIntentManager _assets;
    private readonly LightningSwapClient _lightningSend;
    private readonly LightningReceiveClient _lightningReceive;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly ILogger<ArkadeIntentsService>? _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="assets">The asset-swap corridors.</param>
    /// <param name="lightningSend">The <c>arkade:BTC-&gt;lightning:BTC</c> corridor.</param>
    /// <param name="lightningReceive">The <c>lightning:BTC-&gt;arkade:BTC</c> corridor.</param>
    /// <param name="intentStorage">Where every kind of swap is recorded.</param>
    /// <param name="logger">Optional logger.</param>
    public ArkadeIntentsService(
        ArkadeIntentManager assets,
        LightningSwapClient lightningSend,
        LightningReceiveClient lightningReceive,
        IArkadeIntentStorage intentStorage,
        ILogger<ArkadeIntentsService>? logger = null)
    {
        _assets = assets;
        _lightningSend = lightningSend;
        _lightningReceive = lightningReceive;
        _intentStorage = intentStorage;
        _logger = logger;
    }

    // ─── Creating ─────────────────────────────────────────────────────

    /// <summary>Deposit BTC for an Arkade asset, or the reverse.</summary>
    /// <param name="request">The swap to offer.</param>
    /// <param name="cancellationToken">Cancels before funding.</param>
    /// <returns>The recorded intent.</returns>
    public Task<ArkadeSwapIntent> CreateAssetSwapAsync(
        CreateSwapRequest request, CancellationToken cancellationToken = default) =>
        _assets.CreateSwap(request, cancellationToken);

    /// <summary>Pay a BOLT11 out of an Arkade balance.</summary>
    /// <param name="walletId">The wallet paying.</param>
    /// <param name="invoice">The BOLT11 to pay.</param>
    /// <param name="rfqTransport">How to reach a solver.</param>
    /// <param name="cancellationToken">Cancels before funding.</param>
    /// <returns>The funded swap.</returns>
    public Task<FundedLightningSwap> SendToLightningAsync(
        string walletId,
        string invoice,
        IRfqTransport rfqTransport,
        CancellationToken cancellationToken = default) =>
        _lightningSend.SendToLightningAsync(walletId, invoice, rfqTransport, cancellationToken);

    /// <summary>Be paid over Lightning and take delivery on Arkade.</summary>
    /// <param name="walletId">The wallet taking delivery.</param>
    /// <param name="amountSats">What to receive, in sats.</param>
    /// <param name="rfqTransport">How to reach a solver.</param>
    /// <param name="covclaimdPubKey">covclaimd's key, read live.</param>
    /// <param name="cancellationToken">Cancels the negotiation.</param>
    /// <returns>The invoice to hand to a payer, and what is needed to claim.</returns>
    public Task<PendingLightningReceive> ReceiveFromLightningAsync(
        string walletId,
        long amountSats,
        IRfqTransport rfqTransport,
        string covclaimdPubKey,
        CancellationToken cancellationToken = default) =>
        _lightningReceive.ReceiveFromLightningAsync(
            walletId, amountSats, rfqTransport, covclaimdPubKey, cancellationToken);

    // ─── Reading ──────────────────────────────────────────────────────

    /// <summary>Every swap, whatever corridor it belongs to.</summary>
    /// <param name="status">Narrow to one status.</param>
    /// <param name="walletId">Narrow to one wallet.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The matching swaps.</returns>
    public Task<IReadOnlyCollection<ArkadeSwapIntent>> ListAsync(
        ArkadeSwapIntentStatus? status = null,
        string? walletId = null,
        CancellationToken cancellationToken = default) =>
        _intentStorage.GetArkadeSwapIntents(
            status: status,
            walletIds: walletId is null ? null : [walletId],
            cancellationToken: cancellationToken);

    /// <summary>One swap by id, whatever corridor it belongs to.</summary>
    /// <param name="swapId">The correlation id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The swap, or <c>null</c>.</returns>
    public async Task<ArkadeSwapIntent?> GetAsync(
        string swapId, CancellationToken cancellationToken = default) =>
        (await _intentStorage.GetArkadeSwapIntents(cancellationToken: cancellationToken))
        .FirstOrDefault(s => s.Id == swapId);

    // ─── Acting ───────────────────────────────────────────────────────

    /// <summary>Cancel a pending asset swap and take the deposit back.</summary>
    /// <param name="swapId">The swap to cancel.</param>
    /// <param name="cancellationToken">Cancels before spending.</param>
    /// <returns>The updated intent.</returns>
    /// <remarks>
    /// Deliberately not something <see cref="AdvanceAsync"/> will ever do on its own: a pending swap
    /// is waiting to be filled, which is what was asked for.
    /// </remarks>
    public Task<ArkadeSwapIntent> CancelAssetSwapAsync(
        string swapId, CancellationToken cancellationToken = default) =>
        _assets.CancelSwap(swapId, cancellationToken);

    /// <summary>
    /// Do whatever this swap's kind and status call for, if anything.
    /// </summary>
    /// <param name="swapId">The swap.</param>
    /// <param name="cancellationToken">Cancels before the spend.</param>
    /// <returns>What was decided and whether it ran.</returns>
    /// <exception cref="InvalidOperationException">No such swap.</exception>
    /// <remarks>
    /// Failures are returned rather than thrown, because the useful caller is a loop over many
    /// swaps and one that cannot proceed must not stop the others. A swap that needs nothing comes
    /// back with <see cref="ArkadeIntentAction.None"/> and <c>Acted: false</c>, which is a normal
    /// answer rather than a problem.
    /// </remarks>
    public async Task<ArkadeIntentAdvance> AdvanceAsync(
        string swapId, CancellationToken cancellationToken = default)
    {
        var intent = await GetAsync(swapId, cancellationToken)
            ?? throw new InvalidOperationException($"Swap '{swapId}' not found.");

        var action = ArkadeIntentPolicy.NextAction(intent);
        if (action == ArkadeIntentAction.None)
        {
            return new ArkadeIntentAdvance(swapId, action, Acted: false);
        }

        try
        {
            var updated = action switch
            {
                ArkadeIntentAction.ClaimReceive =>
                    await _lightningReceive.ClaimAsync(swapId, cancellationToken),
                ArkadeIntentAction.RefundSend =>
                    await _lightningSend.RefundSwap(swapId, cancellationToken),
                _ => throw new InvalidOperationException($"unhandled action {action}"),
            };

            _logger?.LogInformation("Swap {SwapId}: {Action} → {Txid}", swapId, action, updated.SpentTxid);
            return new ArkadeIntentAdvance(swapId, action, Acted: true, updated.SpentTxid);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // The status said act and the corridor disagreed — a race with the counterparty, or a
            // window that closed between the two reads. Worth reporting, not worth crashing a sweep.
            _logger?.LogWarning(e, "Swap {SwapId}: {Action} could not run", swapId, action);
            return new ArkadeIntentAdvance(swapId, action, Acted: false, Error: e.Message);
        }
    }

    /// <summary>
    /// Advance every swap that calls for it.
    /// </summary>
    /// <param name="walletId">Narrow to one wallet.</param>
    /// <param name="cancellationToken">Cancels between swaps.</param>
    /// <returns>One result per swap that needed something, in the order attempted.</returns>
    /// <remarks>
    /// Both statuses this acts on are time-bounded — a claim window closes, a refund competes with
    /// nothing but is still money left lying about — so this is meant to be run on a timer, not once.
    /// </remarks>
    public async Task<IReadOnlyList<ArkadeIntentAdvance>> AdvanceAllAsync(
        string? walletId = null, CancellationToken cancellationToken = default)
    {
        var results = new List<ArkadeIntentAdvance>();

        foreach (var intent in await ListAsync(walletId: walletId, cancellationToken: cancellationToken))
        {
            if (ArkadeIntentPolicy.NextAction(intent) == ArkadeIntentAction.None) continue;
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await AdvanceAsync(intent.Id, cancellationToken));
        }

        return results;
    }
}
