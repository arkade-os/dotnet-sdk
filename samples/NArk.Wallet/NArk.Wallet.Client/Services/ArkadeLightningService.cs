using System.Net.Http.Json;
using System.Text.Json;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents.SolverRegistry;
using NBitcoin;

namespace NArk.Wallet.Client.Services;

/// <summary>
/// Where this wallet's Lightning corridors are pointed.
/// </summary>
/// <remarks>
/// <para>
/// One solver, named outright by its Nostr key, because that is the shortest thing a sample can
/// show. Discovery is the other way and it does work: a solver's card carries both halves —
/// <c>DiscoveryPubkey</c> for who to address, <c>Transports.Nostr.Relays</c> for where — and the
/// reducer propagates both into the per-network index, so a discovered corridor is dialable without
/// fetching anything else.
/// </para>
/// <para>
/// <see cref="CovclaimdUrl"/> is what makes receiving safe to walk away from; see
/// <see cref="ArkadeLightningService.CreateInvoiceAsync"/>.
/// </para>
/// </remarks>
public sealed class ArkadeLightningOptions
{
    /// <summary>
    /// The relay to fall back on when the discovered market names none.
    /// </summary>
    /// <remarks>
    /// A fallback, not the route: the market entry's own <c>transports</c> is preferred wherever it
    /// carries one, so this only covers a card written before transports were required.
    /// </remarks>
    public Uri RelayUrl { get; set; } = new("wss://nostr.arkade.sh");

    /// <summary>Base address of a covclaimd instance, or <c>null</c> to claim from this wallet only.</summary>
    public Uri? CovclaimdUrl { get; set; }

    /// <summary>
    /// Trade with this solver instead of asking the registry, when set.
    /// </summary>
    /// <remarks>
    /// An escape hatch for development, not a way to configure a deployment. The registry indexes
    /// no Lightning market on some networks yet, so without this there is nothing to test against;
    /// with it, a solver you are running yourself is reachable by its Nostr key. Set it in a local
    /// <c>appsettings.Development.json</c> that stays out of the repository — a solver's identity
    /// is its operator's to publish, not ours to ship.
    /// </remarks>
    public string? SolverNostrPubkeyOverride { get; set; }
}

/// <summary>
/// The wallet's two Lightning corridors, run over Arkade intents against one solver.
/// </summary>
/// <remarks>
/// <para>
/// Both directions are the same shape: a covenant is funded on Arkade, and a preimage decides who
/// takes it. Sending, we fund and the solver claims by revealing the preimage that paying our
/// invoice produced. Receiving, the solver funds first and we claim, and our claim is what lets the
/// solver settle the payment it is holding. Neither side is ever owed anything on trust.
/// </para>
/// <para>
/// Nothing here is swap-provider-shaped: there is no account with the counterparty, no per-swap
/// websocket to keep open, and no status string to believe. The solver is reached per negotiation
/// over an encrypted, ephemeral Nostr exchange, and what actually happened is read off the covenant
/// rather than reported by the party with an interest in the answer.
/// </para>
/// </remarks>
public sealed class ArkadeLightningService(
    ArkadeLightningOptions options,
    ArkadeIntentsService intents,
    SolverDiscoveryService discovery,
    string networkName)
{
    private (string Pubkey, Uri Relay)? _rendezvous;

    /// <summary>
    /// Finds a solver serving a Lightning corridor on this network, from the public registry.
    /// </summary>
    /// <remarks>
    /// The index is the whole source of truth about who exists; nothing about a counterparty is
    /// baked into this build. An entry carries both halves of the rendezvous — the key to address
    /// and the relays to meet on — so the only thing configuration still supplies is a fallback for
    /// an entry that names no relay.
    /// </remarks>
    private async Task<(string Pubkey, Uri Relay)?> RendezvousAsync(CancellationToken cancellationToken)
    {
        if (_rendezvous is not null) return _rendezvous;
        if (!string.IsNullOrWhiteSpace(options.SolverNostrPubkeyOverride))
        {
            return _rendezvous = (options.SolverNostrPubkeyOverride, options.RelayUrl);
        }

        var markets = await discovery.DiscoverMarketsAsync(networkName, cancellationToken: cancellationToken);
        var market = markets.FirstOrDefault(m =>
            string.Equals(m.QuoteCorridor, "lightning", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(m.DiscoveryPubkey));

        if (market?.DiscoveryPubkey is not { Length: > 0 } pubkey) return null;

        var relay = market.Transports?.Nostr?.Relays is [var advertised, ..]
                    && Uri.TryCreate(advertised, UriKind.Absolute, out var parsed)
            ? parsed
            : options.RelayUrl;

        return _rendezvous = (pubkey, relay);
    }

    /// <summary>Whether a solver has been configured, i.e. whether these corridors are usable at all.</summary>
    /// <summary>Whether a solver serving a Lightning corridor was found on this network.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        await RendezvousAsync(cancellationToken) is not null;

    /// <summary>
    /// Whether a claim will still happen if this wallet is closed during the window.
    /// </summary>
    /// <remarks>
    /// Receiving works either way — this is about who is watching, not about whether the corridor
    /// is open. See <see cref="CreateInvoiceAsync"/>.
    /// </remarks>
    public bool HasOfflineClaimCover => options.CovclaimdUrl is not null;

    /// <summary>Pay a BOLT11 out of the wallet's Arkade balance.</summary>
    /// <param name="walletId">The wallet paying.</param>
    /// <param name="bolt11">The invoice to pay.</param>
    /// <param name="cancellationToken">Cancels before funding.</param>
    /// <returns>The funded swap, including the address the sats went to and the refund address.</returns>
    /// <remarks>
    /// Returns once the covenant is funded, which is not the same as the invoice being paid. The
    /// solver still has to route the payment, and only the preimage it must publish to take the sats
    /// proves that it did — so the swap is reported as sent when that preimage appears, not when
    /// this returns.
    /// </remarks>
    public async Task<FundedLightningSend> PayInvoiceAsync(
        string walletId, string bolt11, CancellationToken cancellationToken = default)
    {
        using var rfq = await CreateTransportAsync(cancellationToken);
        return await intents.SendToLightningAsync(walletId, bolt11, rfq, null, cancellationToken);
    }

    /// <summary>Mint an invoice whose payment lands in this wallet as Arkade sats.</summary>
    /// <param name="walletId">The wallet taking delivery.</param>
    /// <param name="amountSats">What to receive, in sats.</param>
    /// <param name="cancellationToken">Cancels the negotiation.</param>
    /// <returns>The invoice to hand out, and what is needed to claim.</returns>
    /// <remarks>
    /// <para>
    /// Claiming is this wallet's job and always has been: it holds the preimage, and publishing it
    /// is what both takes delivery and lets the solver settle the payment it is holding. covclaimd
    /// never had a key we lack — it is a second pair of eyes, not a co-signer.
    /// </para>
    /// <para>
    /// So the daemon is optional, and what it buys is precisely one thing: somebody claims when
    /// this wallet is closed. Configured, it races our own claim exactly as the Boltz covenant-claim
    /// path does, and whichever gets there first makes the other a no-op. Unconfigured, the window
    /// is ours to meet — a couple of hours, after which the solver reclaims its lockup and cancels
    /// the held invoice, so the payer is refunded and the payment simply never happened.
    /// </para>
    /// </remarks>
    public async Task<PendingLightningReceive> CreateInvoiceAsync(
        string walletId, long amountSats, CancellationToken cancellationToken = default)
    {
        var claimRecipient = await ResolveClaimRecipientAsync(cancellationToken);
        using var rfq = await CreateTransportAsync(cancellationToken);
        return await intents.ReceiveFromLightningAsync(
            walletId, amountSats, rfq, claimRecipient, cancellationToken: cancellationToken);
    }

    /// <summary>Take delivery of a funded receive swap now, rather than waiting for a sweep.</summary>
    /// <param name="swapId">The swap to claim.</param>
    /// <param name="cancellationToken">Cancels before the spend.</param>
    /// <returns>The updated intent.</returns>
    public Task<ArkadeSwapIntent> ClaimAsync(
        string swapId, CancellationToken cancellationToken = default) =>
        intents.ClaimLightningReceiveAsync(swapId, cancellationToken);

    /// <summary>Take back the deposit on a send swap the solver never filled.</summary>
    /// <param name="swapId">The swap to refund.</param>
    /// <param name="cancellationToken">Cancels before the spend.</param>
    /// <returns>The updated intent.</returns>
    /// <remarks>
    /// Only possible once the covenant's refund locktime has passed — before that the solver may
    /// still be routing, and the sats are not ours to take back yet.
    /// </remarks>
    public Task<ArkadeSwapIntent> RefundAsync(
        string swapId, CancellationToken cancellationToken = default) =>
        intents.RefundLightningSendAsync(swapId, cancellationToken);

    /// <summary>Every Lightning swap this wallet has, newest first.</summary>
    /// <param name="walletId">The wallet.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The swaps.</returns>
    public async Task<IReadOnlyList<ArkadeSwapIntent>> ListAsync(
        string walletId, CancellationToken cancellationToken = default)
    {
        var all = await intents.ListAsync(walletId: walletId, cancellationToken: cancellationToken);
        return all
            .Where(i => i.Type is ArkadeSwapIntentType.BtcToLightning
                                or ArkadeSwapIntentType.LightningToBtc)
            .OrderByDescending(i => i.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// A transport aimed at the configured solver over the configured relay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built per negotiation and disposed with it. The RFQ kinds are ephemeral, so the relay stores
    /// nothing and there is no backlog a longer-lived connection could catch up from — a subscription
    /// either exists when the reply is published or misses it. Nothing is gained by holding one open
    /// between swaps, and a socket kept alive across a sleeping browser tab is a liability.
    /// </para>
    /// <para>
    /// No identity key is passed, so each negotiation signs with a fresh one and the relay operator
    /// cannot link this wallet's swaps to each other. No solver card is passed either — that check
    /// holds a quote against published terms, and a solver named in configuration has published none
    /// here to be held to.
    /// </para>
    /// </remarks>
    private async Task<NostrRfqTransport> CreateTransportAsync(CancellationToken cancellationToken)
    {
        var rendezvous = await RendezvousAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"no solver on the {networkName} registry serves a Lightning corridor, so there is "
                + "nobody to quote against");

        return new NostrRfqTransport(rendezvous.Relay, rendezvous.Pubkey);
    }

    /// <summary>
    /// Who the claim packet is sealed to — covclaimd when there is one, nobody when there is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// covclaimd's key is read live, every time, and deliberately not cached: the daemon generates
    /// it at startup, so a restart invalidates any copy. Sealing to a stale key fails silently — the
    /// swap works, and only its offline claim path quietly does not exist.
    /// </para>
    /// <para>
    /// Without a daemon the packet still has to be there: the field is required on the wire, though
    /// the solver treats it as opaque and never opens it. So it is sealed to a key generated here
    /// and immediately dropped, which is the honest encoding of "nobody else is claiming this" —
    /// the sats are reachable by our preimage alone, and that is stored before the invoice is handed
    /// out. Sending a decryptable packet nobody was meant to read would be the stranger choice.
    /// </para>
    /// </remarks>
    private async Task<string> ResolveClaimRecipientAsync(CancellationToken cancellationToken)
    {
        if (options.CovclaimdUrl is not { } covclaimd)
        {
            return new Key().PubKey.Compress().ToHex();
        }

        using var http = new HttpClient { BaseAddress = covclaimd };
        var doc = await http.GetFromJsonAsync<JsonElement>(
            "v1/preimage/covclaimd-pubkey", cancellationToken);
        return doc.GetProperty("covclaimd_pub_key").GetString()
            ?? throw new InvalidOperationException("covclaimd returned no public key.");
    }
}
