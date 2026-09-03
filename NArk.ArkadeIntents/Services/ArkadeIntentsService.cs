using Microsoft.Extensions.Logging;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents.Lightning;
using NArk.Core.Transport;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.SolverRegistry;
using NBitcoin;

using NArk.ArkadeIntents.Assets;
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

/// <summary>One swap whose recorded status was behind what the chain says.</summary>
/// <param name="SwapId">The swap.</param>
/// <param name="From">What we thought.</param>
/// <param name="To">What it actually is.</param>
public sealed record ArkadeIntentReconciled(
    string SwapId, ArkadeSwapIntentStatus From, ArkadeSwapIntentStatus To);

/// <summary>What a reconciliation pass found.</summary>
/// <param name="Updated">Swaps whose status was corrected.</param>
/// <param name="FundingUnconfirmed">
/// Swaps recorded before their funding spend, whose lockup has still not appeared.
/// </param>
/// <remarks>
/// The second list is reported rather than acted on. A spend that has not shown up is either still
/// in flight or never landed, and nothing on our side can tell those apart — guessing either way
/// would mean either abandoning a live swap or resurrecting a dead one.
/// </remarks>
public sealed record ArkadeReconciliation(
    IReadOnlyList<ArkadeIntentReconciled> Updated,
    IReadOnlyList<string> FundingUnconfirmed);

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
    private readonly AssetIntentsManager _assets;
    private readonly LightningIntentsClient _lightning;

    /// <summary>The off-board corridor, when one is registered. Optional: it needs L1 access, which
    /// a lightning-only deployment has no reason to wire up.</summary>
    private readonly OnchainIntentsClient? _onchain;
    private readonly IArkadeIntentStorage _intentStorage;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IClientTransport _transport;
    private readonly TimeProvider _time;
    private readonly ILogger<ArkadeIntentsService>? _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="assets">The asset-swap corridors.</param>
    /// <param name="lightning">Both Lightning corridors.</param>
    /// <param name="intentStorage">Where every kind of swap is recorded.</param>
    /// <param name="vtxoStorage">The chain view reconciliation compares against.</param>
    /// <param name="onchain">
    /// The off-board corridor, or <c>null</c>. Absent, an off-board swap's L1 leg is never acted on
    /// — which the advance pass reports rather than hides.
    /// </param>
    /// <param name="time">Clock for the timelock comparisons; defaults to the system clock.</param>
    /// <param name="logger">Optional logger.</param>
    public ArkadeIntentsService(
        AssetIntentsManager assets,
        LightningIntentsClient lightning,
        IArkadeIntentStorage intentStorage,
        IVtxoStorage vtxoStorage,
        IClientTransport transport,
        OnchainIntentsClient? onchain = null,
        TimeProvider? time = null,
        ILogger<ArkadeIntentsService>? logger = null)
    {
        _assets = assets;
        _lightning = lightning;
        _intentStorage = intentStorage;
        _vtxoStorage = vtxoStorage;
        _transport = transport;
        _onchain = onchain;
        _time = time ?? TimeProvider.System;
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
    /// <param name="solverCard">
    /// The solver's published card, when there is one. Supplying it holds the solver to its own
    /// advertised limits and fee: a quote is whatever arrived on a socket, while the card is signed
    /// and tied to a discoverable identity, and only comparing the two catches a solver quoting
    /// differently from how it advertises. Omitting it is not a check skipped but a check that does
    /// not apply — a deployment naming one solver outright has no published terms to hold it to.
    /// </param>
    /// <param name="cancellationToken">Cancels before funding.</param>
    /// <returns>The funded swap.</returns>
    public Task<FundedLightningSend> SendToLightningAsync(
        string walletId,
        string invoice,
        IRfqTransport rfqTransport,
        SolverCard? solverCard = null,
        CancellationToken cancellationToken = default) =>
        _lightning.SendToLightningAsync(walletId, invoice, rfqTransport, solverCard, cancellationToken);

    /// <summary>Be paid over Lightning and take delivery on Arkade.</summary>
    /// <param name="walletId">The wallet taking delivery.</param>
    /// <param name="amountSats">The size to ask for, in sats — of the leg <paramref name="amountSide"/> names.</param>
    /// <param name="rfqTransport">How to reach a solver.</param>
    /// <param name="covclaimdPubKey">covclaimd's key, read live.</param>
    /// <param name="solverCard">The solver's published card, when there is one.</param>
    /// <param name="amountSide">
    /// Which leg <paramref name="amountSats"/> pins, and so who absorbs the solver's spread — what
    /// lands on Arkade (<see cref="RfqAmountSide.To"/>, the default) or what the payer is billed
    /// (<see cref="RfqAmountSide.From"/>). A merchant minting an invoice for an order total wants
    /// the latter.
    /// </param>
    /// <param name="cancellationToken">Cancels the negotiation.</param>
    /// <returns>The invoice to hand to a payer, and what is needed to claim.</returns>
    public Task<PendingLightningReceive> ReceiveFromLightningAsync(
        string walletId,
        long amountSats,
        IRfqTransport rfqTransport,
        string covclaimdPubKey,
        SolverCard? solverCard = null,
        RfqAmountSide amountSide = RfqAmountSide.To,
        CancellationToken cancellationToken = default) =>
        _lightning.ReceiveFromLightningAsync(
            walletId, amountSats, rfqTransport, covclaimdPubKey, solverCard, amountSide, cancellationToken);

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
        await _intentStorage.GetArkadeSwapIntent(swapId, cancellationToken);

    /// <summary>
    /// Ask the solver where it thinks a negotiation stands.
    /// </summary>
    /// <typeparam name="TStatusProfile">The corridor's status-profile shape.</typeparam>
    /// <param name="swapId">The correlation id the negotiation was opened under.</param>
    /// <param name="rfqTransport">How to reach the solver.</param>
    /// <param name="cancellationToken">Cancels the round trip.</param>
    /// <returns>The solver's view, or <c>null</c> when it has no record.</returns>
    /// <remarks>
    /// Diagnostic only, and never the money path — a funded swap is observable on-chain whether or
    /// not the solver answers, which is why nothing here acts on the reply. What it adds is the one
    /// thing the chain cannot express: WHY nothing has happened. Refused, expired and not-yet all
    /// look identical from our side, and only the counterparty can tell them apart.
    /// </remarks>
    public Task<RfqStatus<TStatusProfile>?> GetSolverStatusAsync<TStatusProfile>(
        string swapId,
        IRfqTransport rfqTransport,
        CancellationToken cancellationToken = default) =>
        rfqTransport.GetStatusAsync<TStatusProfile>(swapId, cancellationToken);

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
    /// Push a Lightning send swap's refund back to our own address.
    /// </summary>
    /// <param name="swapId">The swap to refund.</param>
    /// <param name="cancellationToken">Cancels before the spend.</param>
    /// <returns>The updated intent.</returns>
    /// <remarks>
    /// <para>
    /// Takes <c>refundWithoutReceiver</c>: our key plus the Arkade server, once
    /// <c>refund_locktime</c> has passed. Of the covenant's several refund leaves it is the only one
    /// a client can start on its own, and that is worth being precise about rather than discovering
    /// at the moment it is needed:
    /// </para>
    /// <list type="bullet">
    /// <item><c>refund</c> (immediate) and <c>refundWithoutServer</c> (after a CSV) both need the
    /// solver's signature, and the RFQ protocol carries no message asking for one — the solver may
    /// push a refund of its own accord, but that is an operator action on its side, not something
    /// we can request.</item>
    /// <item><c>nonInteractiveRefund</c> is the solver's to push, not ours.</item>
    /// <item><c>unilateralRefundWithoutReceiver</c> needs nobody, but reaching it means unrolling
    /// the VTXO to the chain, and an eight-leaf covenant lands there carrying enough script data
    /// that the exit costs more than it recovers.</item>
    /// </list>
    /// <para>
    /// So this is the recourse, and it is exposed directly rather than only through
    /// <see cref="AdvanceAsync"/>, because wanting the money back now is the caller's call to make
    /// at whatever moment they like — a sweep on a timer is a convenience, not the only way in.
    /// </para>
    /// </remarks>
    public Task<ArkadeSwapIntent> RefundLightningSendAsync(
        string swapId, CancellationToken cancellationToken = default) =>
        _lightning.RefundSwap(swapId, cancellationToken);

    /// <summary>
    /// Claim a funded Lightning receive swap, publishing the preimage.
    /// </summary>
    /// <param name="swapId">The swap to claim.</param>
    /// <param name="cancellationToken">Cancels before the spend.</param>
    /// <returns>The updated intent.</returns>
    /// <remarks>
    /// Exposed directly for the same reason as the refund, and with more urgency: the window closes
    /// when the solver's own reclaim path opens, so a caller who wants to take delivery now should
    /// not have to go through a sweep to do it.
    /// </remarks>
    public Task<ArkadeSwapIntent> ClaimLightningReceiveAsync(
        string swapId, CancellationToken cancellationToken = default) =>
        _lightning.ClaimAsync(swapId, cancellationToken);

    /// <summary>
    /// Off-board an Arkade balance to Bitcoin L1.
    /// </summary>
    /// <param name="walletId">The wallet paying, and receiving any refund.</param>
    /// <param name="payoutAddress">The Bitcoin L1 address the off-board pays out to.</param>
    /// <param name="amountSats">The size, on the leg <paramref name="amountSide"/> names.</param>
    /// <param name="rfqTransport">How to reach a solver.</param>
    /// <param name="amountSide">
    /// Which leg <paramref name="amountSats"/> pins, and so who absorbs the solver's spread — what
    /// lands on L1 (<see cref="RfqAmountSide.To"/>, the default) or what leaves the Arkade balance.
    /// </param>
    /// <param name="solverCard">The solver's published card, when there is one.</param>
    /// <param name="cancellationToken">Cancels before funding.</param>
    /// <returns>The funded swap.</returns>
    /// <exception cref="InvalidOperationException">No onchain corridor is registered.</exception>
    /// <remarks>
    /// The corridor's own entry point was reachable only by resolving <see cref="OnchainIntentsClient"/>
    /// directly, while its claim and refund were already driven through here — so the one facade that
    /// exists to spare callers knowing which class owns a swap could start every corridor but this one.
    /// </remarks>
    public Task<FundedOnchainSend> SendToOnchainAsync(
        string walletId,
        BitcoinAddress payoutAddress,
        long amountSats,
        IRfqTransport rfqTransport,
        RfqAmountSide amountSide = RfqAmountSide.To,
        SolverCard? solverCard = null,
        CancellationToken cancellationToken = default) =>
        RequireOnchain().SendToOnchainAsync(
            walletId, payoutAddress, amountSats, amountSide, rfqTransport, solverCard, cancellationToken);

    /// <summary>
    /// On-board Bitcoin L1 sats into an Arkade balance.
    /// </summary>
    /// <param name="walletId">The wallet taking delivery.</param>
    /// <param name="amountSats">The size to ask for, on the leg <paramref name="amountSide"/> names.</param>
    /// <param name="rfqTransport">How to reach a solver.</param>
    /// <param name="covclaimdPubKey">covclaimd's key, read live.</param>
    /// <param name="l1RefundAddress">Where the L1 HTLC pays if it has to be taken back.</param>
    /// <param name="amountSide">
    /// Which leg <paramref name="amountSats"/> pins, and so who absorbs the solver's spread — what we
    /// send on L1 (<see cref="RfqAmountSide.From"/>, the default) or what lands on Arkade.
    /// </param>
    /// <param name="solverCard">The solver's published card, when there is one.</param>
    /// <param name="cancellationToken">Cancels the negotiation.</param>
    /// <returns>The L1 address to fund, and what is needed to claim afterwards.</returns>
    /// <exception cref="InvalidOperationException">No onchain corridor is registered.</exception>
    /// <remarks>
    /// Funds nothing itself: the L1 funding transaction belongs to the caller's own Bitcoin wallet,
    /// since the sats being on-boarded are by definition not on Arkade yet.
    /// </remarks>
    public Task<PendingOnchainReceive> ReceiveFromOnchainAsync(
        string walletId,
        long amountSats,
        IRfqTransport rfqTransport,
        string covclaimdPubKey,
        BitcoinAddress l1RefundAddress,
        RfqAmountSide amountSide = RfqAmountSide.From,
        SolverCard? solverCard = null,
        CancellationToken cancellationToken = default) =>
        RequireOnchain().ReceiveFromOnchainAsync(
            walletId, amountSats, rfqTransport, covclaimdPubKey, l1RefundAddress,
            amountSide, solverCard, cancellationToken);

    /// <summary>
    /// Claim a funded on-board, publishing the preimage.
    /// </summary>
    /// <param name="swapId">The swap to claim.</param>
    /// <param name="cancellationToken">Cancels before the spend.</param>
    /// <returns>The updated intent.</returns>
    /// <remarks>
    /// Exposed directly for the same reason the Lightning claim is, and with the same urgency: the
    /// window closes when the solver's own reclaim opens.
    /// </remarks>
    public Task<ArkadeSwapIntent> ClaimOnchainReceiveAsync(
        string swapId, CancellationToken cancellationToken = default) =>
        RequireOnchain().ClaimOnchainReceiveAsync(swapId, cancellationToken);

    /// <summary>
    /// Take back an on-board's L1 funding once its refund leaf has matured.
    /// </summary>
    /// <param name="swapId">The swap to refund.</param>
    /// <param name="refundAddress">Where to pay, overriding the address recorded at negotiation.</param>
    /// <param name="cancellationToken">Cancels before the broadcast.</param>
    /// <returns>What the attempt found; not refunding yet is an ordinary answer.</returns>
    /// <remarks>
    /// The on-board's only recourse. Unlike the send corridors there is no Arkade covenant to refund
    /// — nothing of ours was ever funded there — so if the solver never delivers, this is the way the
    /// sats come home.
    /// </remarks>
    public Task<OnchainRefundOutcome> RefundOnchainReceiveAsync(
        string swapId,
        BitcoinAddress? refundAddress = null,
        CancellationToken cancellationToken = default) =>
        RequireOnchain().RefundOnchainReceiveAsync(swapId, refundAddress, cancellationToken);

    /// <summary>The onchain corridor, or a refusal naming what is missing.</summary>
    /// <remarks>
    /// The corridor is optional in the container because it needs L1 access a Lightning-only
    /// deployment has no reason to wire up. A caller reaching for it anyway should be told that,
    /// rather than handed a <c>NullReferenceException</c> from inside a facade.
    /// </remarks>
    private OnchainIntentsClient RequireOnchain() =>
        _onchain ?? throw new InvalidOperationException(
            "no onchain corridor is registered — it needs an IBitcoinBlockchain, which this "
            + "deployment has not supplied");

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
            // Handled apart from the others because its ordinary answer is "not yet": the L1 leg it
            // watches produces no event, so this action is proposed on every pass and most passes
            // find nothing to do. That is an outcome, not a failure, and it moves no swap.
            if (action == ArkadeIntentAction.ClaimOnchain)
            {
                if (_onchain is null)
                {
                    return new ArkadeIntentAdvance(
                        swapId, action, Acted: false,
                        Error: "no onchain corridor is registered to act on this swap");
                }

                var outcome = await _onchain.ClaimOnchainAsync(swapId, cancellationToken: cancellationToken);
                if (outcome.Claimed)
                {
                    _logger?.LogInformation("Swap {SwapId}: claimed on L1 in {Txid}", swapId, outcome.Txid);
                }
                return new ArkadeIntentAdvance(swapId, action, outcome.Claimed, outcome.Txid, outcome.Detail);
            }

            // Same shape as the off-board's claim, and for the same reason: the L1 median time past
            // it waits on produces no event, so this is proposed on every pass and most passes find
            // nothing due. Also the on-board's ONLY recourse — it funded nothing on Arkade, so there
            // is no covenant refund to fall back to.
            if (action == ArkadeIntentAction.RefundOnchain)
            {
                if (_onchain is null)
                {
                    return new ArkadeIntentAdvance(
                        swapId, action, Acted: false,
                        Error: "no onchain corridor is registered to act on this swap");
                }

                var refund = await _onchain.RefundOnchainReceiveAsync(
                    swapId, cancellationToken: cancellationToken);
                if (refund.Refunded)
                {
                    _logger?.LogInformation("Swap {SwapId}: refunded on L1 in {Txid}", swapId, refund.Txid);
                }
                return new ArkadeIntentAdvance(swapId, action, refund.Refunded, refund.Txid, refund.Detail);
            }

            var updated = action switch
            {
                // One action, two corridors. Both receive legs claim the same covenant with the same
                // preimage; which client owns the row is bookkeeping, not a difference in what the
                // caller asked for.
                ArkadeIntentAction.ClaimReceive when intent.Type == ArkadeSwapIntentType.OnchainToBtc =>
                    _onchain is not null
                        ? await _onchain.ClaimOnchainReceiveAsync(swapId, cancellationToken)
                        : throw new InvalidOperationException(
                            "no onchain corridor is registered to claim this swap"),
                ArkadeIntentAction.ClaimReceive =>
                    await _lightning.ClaimAsync(swapId, cancellationToken),
                ArkadeIntentAction.RefundSend =>
                    await _lightning.RefundSwap(swapId, cancellationToken),
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
    /// The preimage the spend of a lockup revealed, if it revealed one.
    /// </summary>
    /// <remarks>
    /// Answers <c>null</c> for anything that is not a spent Lightning swap with a payment hash to
    /// check against: there is nothing to prove in those cases, and asking the indexer for a
    /// transaction that cannot help is a round trip spent on a foregone answer.
    /// </remarks>
    private async Task<byte[]?> RevealedPreimageAsync(
        ArkadeSwapIntent intent, ArkVtxo lockup, CancellationToken cancellationToken)
    {
        // Every HTLC-class corridor, not only the Lightning pair. The onchain legs settle against
        // the same covenant and their fill is proved the same way, so leaving them out meant a
        // solver's claim of an off-board lockup read back as `Resolved` — "the script moved, we
        // cannot say why" — when the witness in front of us said exactly why.
        if (!lockup.IsSpent()
            || intent.PaymentHash is not { Length: > 0 } hash
            || intent.Type is not (ArkadeSwapIntentType.BtcToLightning or ArkadeSwapIntentType.LightningToBtc
                or ArkadeSwapIntentType.BtcToOnchain or ArkadeSwapIntentType.OnchainToBtc))
        {
            return null;
        }

        var spender = lockup.SpentByTransactionId ?? lockup.SettledByTransactionId;
        return spender is not { Length: > 0 }
            ? null
            : await SwapPreimageReader.FindAsync(_transport, lockup.OutPoint, spender, hash, cancellationToken);
    }

    /// <summary>
    /// Re-derive every open swap's status from the chain, and report what was behind.
    /// </summary>
    /// <param name="walletId">Narrow to one wallet.</param>
    /// <param name="cancellationToken">Cancels between swaps.</param>
    /// <returns>What was corrected, and what is still unconfirmed.</returns>
    /// <remarks>
    /// The monitor only ever reacts to changes it is present for, so anything that happened while
    /// this process was down is missed permanently — a claim window can open and close in that gap.
    /// Run this at startup, before the first <see cref="AdvanceAllAsync"/>, or the sweep acts on a
    /// picture that stopped being true when the process did.
    /// </remarks>
    public async Task<ArkadeReconciliation> ReconcileAsync(
        string? walletId = null, CancellationToken cancellationToken = default)
    {
        var updated = new List<ArkadeIntentReconciled>();
        var unconfirmed = new List<string>();
        var now = _time.GetUtcNow().ToUnixTimeSeconds();

        foreach (var intent in await ListAsync(walletId: walletId, cancellationToken: cancellationToken))
        {
            // Resolved is the one terminal status worth re-examining: it may have been recorded on
            // a transient read failure, before the spending transaction was fetchable, and a
            // preimage found now upgrades it to the fill it always was.
            if (ArkadeSwapStateMachine.Terminal.Contains(intent.Status)
                && intent.Status != ArkadeSwapIntentStatus.Resolved) continue;
            cancellationToken.ThrowIfCancellationRequested();

            // includeSpent matters: a lockup the counterparty already took is exactly the outcome
            // this pass exists to notice, and the default view hides it.
            var vtxos = await _vtxoStorage.GetVtxos(
                scripts: [intent.SwapPkScript], includeSpent: true, cancellationToken: cancellationToken);

            // Prefer the live output; fall back to a spent one, which still carries the outcome.
            var lockup = vtxos.FirstOrDefault(v => !v.IsSpent() && !v.Swept) ?? vtxos.FirstOrDefault();
            if (lockup is null)
            {
                if (intent.Status == ArkadeSwapIntentStatus.Funding) unconfirmed.Add(intent.Id);
                continue;
            }

            var preimage = await RevealedPreimageAsync(intent, lockup, cancellationToken);

            ArkadeSwapIntentStatus? next = intent.Status == ArkadeSwapIntentStatus.Resolved
                // Terminal to the machine, so the upgrade is decided here: a proven preimage on a
                // swap written off as resolved means the earlier read was wrong, not that the
                // swap reopened.
                ? preimage is not null ? ArkadeSwapIntentStatus.Fulfilled : null
                : ArkadeSwapStateMachine.Next(
                    intent.Type, intent.Status,
                    SwapObservation.From(lockup, now, intent.RefundLocktime, preimage is not null));
            if (next is null) continue;

            var from = intent.Status;
            intent.Status = next.Value;
            if (lockup.IsSpent())
            {
                intent.SpentTxid ??= lockup.ArkTxid ?? lockup.SpentByTransactionId;
            }
            await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);

            _logger?.LogInformation("Swap {SwapId} reconciled: {From} → {To}", intent.Id, from, next.Value);
            updated.Add(new ArkadeIntentReconciled(intent.Id, from, next.Value));
        }

        return new ArkadeReconciliation(updated, unconfirmed);
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
        var now = _time.GetUtcNow().ToUnixTimeSeconds();

        foreach (var intent in await ListAsync(walletId: walletId, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Deadlines raise no chain event, so the monitor never sees them: a lockup sitting
            // unspent past its locktime is only ever noticed by a pass that checks the clock.
            // Without this a send swap would never become Refundable, and a receive swap whose
            // claim window closed would be retried forever.
            if (ArkadeSwapStateMachine.NextOnClock(intent.Type, intent.Status, now, intent.RefundLocktime)
                    is { } timed)
            {
                _logger?.LogInformation("Swap {SwapId}: {From} → {To} on the clock",
                    intent.Id, intent.Status, timed);
                intent.Status = timed;
                await _intentStorage.SaveArkadeSwapIntent(intent, cancellationToken);
            }

            if (ArkadeIntentPolicy.NextAction(intent) == ArkadeIntentAction.None) continue;
            results.Add(await AdvanceAsync(intent.Id, cancellationToken));
        }

        return results;
    }
}
