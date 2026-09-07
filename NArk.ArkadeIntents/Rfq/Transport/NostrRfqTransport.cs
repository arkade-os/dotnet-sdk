using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NArk.ArkadeIntents.Nostr;
using NBitcoin;

namespace NArk.ArkadeIntents.Rfq;

/// <summary>
/// Thrown when the relay itself refuses or tears down what we asked of it.
/// </summary>
/// <remarks>
/// Separate from a solver's refusal on purpose. "The relay would not take my event" and "the solver
/// declined to quote" are different problems with different fixes, and a transport that reported
/// both as silence is what made the reference deployment's own outage take days to diagnose.
/// </remarks>
public sealed class NostrRelayException(string message) : Exception(message);

/// <summary>
/// Thrown when every relay is gone, so no reply could have reached us on any of them.
/// </summary>
/// <remarks>
/// Distinct from a timeout on purpose, and the distinction is the whole reason this type exists.
/// Both look like "no answer", and they mean opposite things: a timeout says the solver did not
/// respond, while this says we were never in a position to hear it. Without the difference a client
/// waits out the full timeout and then blames a counterparty for a failure on its own side of the
/// wire — which is a bug report filed against the wrong party, and the reference deployment lost
/// days to exactly that.
/// </remarks>
public sealed class RelayUnavailableException(IReadOnlyList<string> reasons)
    : Exception($"lost every relay connection: {string.Join("; ", reasons)}")
{
    /// <summary>Why each relay was unusable, in the order they were tried.</summary>
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>
/// Thrown when the transport was disposed while a negotiation was still waiting for a reply.
/// </summary>
/// <remarks>
/// Describes a decision on our own side of the wire rather than anything about the wire. A caller
/// that closed deliberately — a user leaving the screen, a flow abandoning its request — can match
/// on this and stay quiet, instead of reporting a solver failure that never happened.
/// </remarks>
public sealed class TransportClosedException()
    : Exception("the transport was closed before the solver replied");

/// <summary>
/// The RFQ transport the protocol actually specifies: NIP-01 over a relay, addressed by pubkey.
/// </summary>
/// <remarks>
/// <para>
/// Both parties dial out and neither listens, which is what lets a solver run with no inbound port
/// and no DNS name. It is also the only way to use the registry's rendezvous data, since a corridor
/// card carries a discovery pubkey and relays rather than a URL.
/// </para>
/// <para>
/// Each negotiation uses a fresh identity key by default, which keeps separate swaps unlinkable to
/// the relay operator and is also what keeps a busy client quotable: solvers meter quote creation
/// per requester identity — the author key, on a relay — so one stable key spends a shared quota
/// and starts drawing <c>rate_limited</c> refusals. A stable key costs one fewer ECDH per
/// negotiation, which is the dominant per-message cost here, and is worth passing only where the
/// traffic is low enough that the quota is not the binding constraint.
/// </para>
/// </remarks>
public sealed class NostrRfqTransport : IRfqTransport, IDisposable
{
    /// <summary>
    /// Directed RFQ traffic. In NIP-01's <em>ephemeral</em> range, so relays forward it without
    /// storing it. Provisional, per the protocol spec.
    /// </summary>
    /// <remarks>
    /// The range matters to how this transport is written. Nothing is retained, so there is no
    /// backlog to catch up from — a subscription that is not already live when the reply is
    /// published misses it outright. Hence subscribing before publishing rather than after.
    /// </remarks>
    public const int DirectedKind = 24859;

    /// <summary>Open-RFQ broadcasts. Same range, same retention: none.</summary>
    public const int BroadcastKind = 24860;

    private readonly IReadOnlyList<Uri> _relays;
    private readonly byte[] _solverPubkey;
    private readonly Key? _identity;
    private readonly TimeSpan _timeout;
    private readonly ILogger<NostrRfqTransport>? _logger;

    /// <summary>Cancelled by <see cref="Dispose"/>, so a waiting negotiation can say why it ended.</summary>
    private readonly CancellationTokenSource _closed = new();

    /// <summary>Creates the transport against a single relay.</summary>
    /// <param name="relay">The relay to dial, e.g. <c>wss://relay.example</c>.</param>
    /// <param name="solverPubkey">The solver's x-only key, hex — its address on the relay.</param>
    /// <param name="identity">
    /// Our own key. Omit for a fresh one per negotiation, which is the more private default.
    /// </param>
    /// <param name="timeout">How long to wait for a reply before giving up. Defaults to 30s.</param>
    /// <param name="logger">Optional logger; relay-level faults are reported here.</param>
    /// <remarks>
    /// A convenience over the relay-set constructor. A card carries a LIST, and dialling one entry
    /// of it makes a single operator's outage look exactly like a solver that declined to answer —
    /// so prefer passing everything the card advertises.
    /// </remarks>
    public NostrRfqTransport(
        Uri relay,
        string solverPubkey,
        Key? identity = null,
        TimeSpan? timeout = null,
        ILogger<NostrRfqTransport>? logger = null)
        : this([relay], solverPubkey, identity, timeout, logger)
    {
    }

    /// <summary>Creates the transport against every relay the solver's card advertises.</summary>
    /// <param name="relays">
    /// The rendezvous set, in the card's own order. Each is dialled and each carries the same
    /// request.
    /// </param>
    /// <param name="solverPubkey">The solver's x-only key, hex — its address on the relay.</param>
    /// <param name="identity">
    /// Our own key. Omit for a fresh one per negotiation, which is the more private default.
    /// </param>
    /// <param name="timeout">How long to wait for a reply before giving up. Defaults to 30s.</param>
    /// <param name="logger">Optional logger; relay-level faults are reported here.</param>
    /// <exception cref="ArgumentException">No relays, or a malformed solver key.</exception>
    /// <remarks>
    /// <para>
    /// Every relay is dialled at once and the first valid reply wins; the rest are torn down. That
    /// is not an optimisation but the point of a relay SET: a rendezvous is a place both parties
    /// happen to be, and neither side controls which of the card's relays the other is actually
    /// connected to at this moment. Dialling one and waiting is a coin flip dressed up as a protocol.
    /// </para>
    /// <para>
    /// The same event goes to all of them — same id, same signature — so a solver connected to
    /// several sees duplicates of one request rather than several requests. The negotiation id makes
    /// that idempotent on its side.
    /// </para>
    /// </remarks>
    public NostrRfqTransport(
        IReadOnlyList<Uri> relays,
        string solverPubkey,
        Key? identity = null,
        TimeSpan? timeout = null,
        ILogger<NostrRfqTransport>? logger = null)
    {
        if (relays.Count == 0)
        {
            throw new ArgumentException("a transport needs at least one relay to dial", nameof(relays));
        }
        _relays = relays;
        _solverPubkey = Convert.FromHexString(solverPubkey);
        if (_solverPubkey.Length != 32)
        {
            throw new ArgumentException(
                $"solver pubkey must be 32 bytes x-only, got {_solverPubkey.Length}", nameof(solverPubkey));
        }
        _identity = identity;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    /// <summary>
    /// Build a transport from a solver's registry card, using every relay it advertises.
    /// </summary>
    /// <param name="card">The card, as published or pinned locally.</param>
    /// <param name="identity">Our own key. Omit for a fresh one per negotiation.</param>
    /// <param name="timeout">How long to wait for a reply. Defaults to 30s.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A transport addressed to the card's discovery key over its whole relay set.</returns>
    /// <exception cref="ArgumentException">
    /// The card carries no discovery key, or no relay this client will dial.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The card is where a relay set comes from — a corridor card is REQUIRED to carry
    /// <c>discovery_pubkey</c> and <c>transports</c>, because its rendezvous is live data a maker
    /// will actually contact. Reading the list and then dialling one entry of it, which is what a
    /// caller writing this by hand tends to do, turns one operator's outage into what looks like a
    /// solver refusing to quote.
    /// </para>
    /// <para>
    /// Non-<c>wss://</c> entries are dropped rather than dialled. The registry's own schema admits
    /// only <c>wss://</c>, so a plaintext entry is either a malformed card or a downgrade someone
    /// wants us to accept — and this traffic is sealed to the solver's key but not to the relay's,
    /// so who carries it is still worth being strict about. Duplicates are collapsed: a card listing
    /// the same relay twice should cost one connection, not two.
    /// </para>
    /// </remarks>
    public static NostrRfqTransport ForCard(
        SolverRegistry.SolverCard card,
        Key? identity = null,
        TimeSpan? timeout = null,
        ILogger<NostrRfqTransport>? logger = null)
    {
        if (card.DiscoveryPubkey is not { Length: > 0 } pubkey)
        {
            throw new ArgumentException(
                $"solver card '{card.Name}' carries no discovery_pubkey, so there is nobody to address",
                nameof(card));
        }

        var relays = (card.Transports?.Nostr?.Relays ?? [])
            .Where(r => Uri.TryCreate(r, UriKind.Absolute, out var parsed)
                        && parsed.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => new Uri(r))
            .ToList();

        if (relays.Count == 0)
        {
            throw new ArgumentException(
                $"solver card '{card.Name}' advertises no wss:// relay, so its discovery key names a "
                + "solver nothing can dial", nameof(card));
        }

        return new NostrRfqTransport(relays, pubkey, identity, timeout, logger);
    }

    /// <inheritdoc />
    public async Task<RfqQuote<TQuoteProfile>> RequestQuoteAsync<TRequestProfile, TQuoteProfile>(
        RfqRequest<TRequestProfile> request,
        CancellationToken cancellationToken = default)
    {
        var reply = await ExchangeAsync(
            JsonSerializer.Serialize(request, RfqProtocol.Json), cancellationToken);

        return RfqProtocol.ExpectQuote<TQuoteProfile>(reply, request.RfqId, request.Pair);
    }

    /// <inheritdoc />
    public async Task<RfqStatus<TStatusProfile>?> GetStatusAsync<TStatusProfile>(
        string rfqId,
        CancellationToken cancellationToken = default)
    {
        var ask = new JsonObject
        {
            ["v"] = RfqProtocol.Version,
            ["type"] = "rfq_status_request",
            ["rfq_id"] = rfqId,
        };

        var reply = await ExchangeAsync(ask.ToJsonString(), cancellationToken);
        return reply["type"]?.GetValue<string>() == "rfq_status"
            ? reply.Deserialize<RfqStatus<TStatusProfile>>(RfqProtocol.Json)
            : null;
    }

    /// <summary>What one relay contributed to a negotiation.</summary>
    /// <param name="Payload">The reply, when this relay carried one.</param>
    /// <param name="Failure">Why it did not, phrased for a human reading a log.</param>
    /// <param name="WasListening">
    /// Whether the subscription was ever live on it. This is what separates "the solver did not
    /// answer" from "we could not have heard it if it had" — a relay that never accepted the
    /// subscription proves nothing about the counterparty.
    /// </param>
    private sealed record RelayOutcome(JsonObject? Payload, string? Failure, bool WasListening);

    /// <summary>
    /// Publish one sealed payload to every relay and take the first reply addressed back.
    /// </summary>
    private async Task<JsonObject> ExchangeAsync(string payload, CancellationToken cancellationToken)
    {
        var identity = _identity ?? new Key();
        var ourPubkey = NostrEventFactory.Sign(identity, DirectedKind, "x").Pubkey;
        var conversationKey = Nip44.GetConversationKey(identity, _solverPubkey);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _closed.Token);
        linked.CancelAfter(_timeout);

        // One event for every relay: same id, same signature. A solver connected to several sees
        // duplicates of one request rather than several requests, which its negotiation id already
        // makes idempotent.
        var sealedContent = Nip44.Encrypt(payload, conversationKey, RandomNumberGenerator.GetBytes(32));
        var ev = NostrEventFactory.Sign(
            identity, DirectedKind, sealedContent,
            [["p", Convert.ToHexString(_solverPubkey).ToLowerInvariant()]]);

        // Cancels the losers the moment one relay answers. Without it the slow ones keep a socket
        // and a read pending until the timeout, on a transport a caller believes is finished.
        using var answered = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);

        var pending = _relays
            .Select(relay => ExchangeOnAsync(relay, ev, ourPubkey, conversationKey, answered.Token))
            .ToList();

        var failures = new List<string>();
        var anyListened = false;

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);

            var outcome = await finished;
            anyListened |= outcome.WasListening;

            if (outcome.Payload is { } reply)
            {
                await answered.CancelAsync();
                return reply;
            }
            if (outcome.Failure is { } failure) failures.Add(failure);
        }

        // Our own doing, and worth saying so rather than blaming the counterparty.
        if (_closed.IsCancellationRequested) throw new TransportClosedException();
        cancellationToken.ThrowIfCancellationRequested();

        // Somebody was listening and nothing came: that IS a statement about the solver.
        if (anyListened)
        {
            throw new NostrRelayException(
                $"no reply from the solver within {_timeout.TotalSeconds:0}s — it may not be connected to "
                + string.Join(", ", _relays.Select(r => r.ToString())));
        }

        // Nobody was listening, so the silence says nothing about the solver at all.
        throw new RelayUnavailableException(failures);
    }

    /// <summary>
    /// Run the whole exchange against one relay, reporting rather than throwing.
    /// </summary>
    /// <remarks>
    /// Never throws for anything a sibling relay could still recover from. One relay refusing our
    /// event, or tearing the subscription down, is that relay's failure — turning it into the
    /// negotiation's failure would let the worst member of a set decide the outcome for all of them,
    /// which is the opposite of why a card advertises several.
    /// </remarks>
    private async Task<RelayOutcome> ExchangeOnAsync(
        Uri relay, NostrEvent ev, string ourPubkey, byte[] conversationKey, CancellationToken ct)
    {
        var listening = false;
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(relay, ct);

            // Subscribe BEFORE publishing. These kinds are ephemeral, so a relay stores nothing and
            // there is no backlog to fall back on: a reply published while we are not yet listening
            // is simply gone. `since` is floored to whole seconds because that is all `created_at`
            // has, and a second of slack costs less than missing a reply minted in the second we
            // subscribed.
            var subId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
            await SendAsync(socket, new JsonArray(
                "REQ", subId,
                new JsonObject
                {
                    ["kinds"] = new JsonArray(DirectedKind),
                    ["#p"] = new JsonArray(ourPubkey),
                    ["since"] = since,
                }), ct);

            listening = true;

            await SendAsync(socket, new JsonArray(
                "EVENT", JsonSerializer.SerializeToNode(ev, NostrEventFactory.Json)), ct);

            try
            {
                return new RelayOutcome(
                    await ReadReplyAsync(socket, ev.Id, conversationKey, ct), null, true);
            }
            finally
            {
                await CloseQuietlyAsync(socket);
            }
        }
        catch (OperationCanceledException)
        {
            // Either the timeout, our own close, or a sibling relay that already answered. None is
            // this relay's fault and none is worth reporting as one.
            return new RelayOutcome(null, null, listening);
        }
        catch (Exception e) when (e is NostrRelayException or WebSocketException or IOException
                                      or ObjectDisposedException)
        {
            _logger?.LogWarning("relay {Relay} unusable: {Reason}", relay, e.Message);
            return new RelayOutcome(null, $"{relay}: {e.Message}", listening);
        }
    }

    /// <summary>Read until this socket yields a reply for us, or runs out of frames.</summary>
    private async Task<JsonObject?> ReadReplyAsync(
        ClientWebSocket socket, string publishedId, byte[] conversationKey, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        while (socket.State == WebSocketState.Open)
        {
            var frame = await ReceiveTextAsync(socket, buffer, ct);
            if (frame is null) break;

            JsonArray? message;
            try
            {
                message = JsonNode.Parse(frame)?.AsArray();
            }
            catch (JsonException)
            {
                continue;
            }
            if (message is null || message.Count == 0) continue;

            switch (message[0]?.GetValue<string>())
            {
                case "EVENT" when message.Count >= 3:
                    var ev = message[2].Deserialize<NostrEvent>(NostrEventFactory.Json);
                    // Anyone may publish anything claiming any author, so nothing is read out of an
                    // event until its own signature says it is what it claims.
                    if (ev is null || !NostrEventFactory.Verify(ev)) continue;

                    string plaintext;
                    try
                    {
                        plaintext = Nip44.Decrypt(ev.Content, conversationKey);
                    }
                    catch (CryptographicException)
                    {
                        // Not sealed to us. On a shared subscription that is ordinary traffic, not
                        // an error.
                        continue;
                    }

                    if (JsonNode.Parse(plaintext) is JsonObject payload) return payload;
                    continue;

                // The relay telling us it would not store our event. Swallowing this is what makes
                // "refused every publish" and "nobody answered" look identical from the outside.
                case "OK" when message.Count >= 3 && message[1]?.GetValue<string>() == publishedId:
                    if (message[2]?.GetValue<bool>() == false)
                    {
                        throw new NostrRelayException(
                            $"the relay rejected our event: {message.ElementAtOrDefault(3)?.GetValue<string>() ?? "no reason given"}");
                    }
                    continue;

                // The relay tore down a subscription we still believe is live. No id check: this
                // socket carries exactly one subscription, so every CLOSED on it is ours. An earlier
                // guard compared the frame's subscription id against the published event id — two
                // different identifiers that never match — and the case below did the same thing
                // anyway, so it described a distinction that did not exist.
                case "CLOSED":
                    throw new NostrRelayException(
                        $"the relay closed our subscription: {message.ElementAtOrDefault(2)?.GetValue<string>() ?? "no reason given"}");

                case "NOTICE":
                    _logger?.LogWarning("relay notice: {Notice}", message.ElementAtOrDefault(1)?.GetValue<string>());
                    continue;

                default:
                    continue;
            }
        }

        // Out of frames without a payload: this socket is done, and whether that is a solver that
        // stayed quiet or a relay that dropped us is not decidable here. The caller knows which
        // relays were listening and answers accordingly.
        return null;
    }

    private static async Task SendAsync(ClientWebSocket socket, JsonArray message, CancellationToken ct) =>
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct);

    /// <summary>Read one complete text frame, reassembling continuations.</summary>
    private static async Task<string?> ReceiveTextAsync(
        ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return sb.ToString();
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        if (socket.State != WebSocketState.Open) return;
        try
        {
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // The exchange already succeeded or failed on its own terms; a rude close changes
            // neither, and the socket is disposed regardless.
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cancels any negotiation still waiting, so it ends as <see cref="TransportClosedException"/>
    /// rather than sitting out the full timeout and then reporting a solver that never failed.
    /// </remarks>
    public void Dispose()
    {
        if (!_closed.IsCancellationRequested) _closed.Cancel();
        _closed.Dispose();
        _identity?.Dispose();
    }
}
