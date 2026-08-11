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
/// the relay operator. The cost is an ECDH per negotiation — the dominant per-message cost in this
/// scheme — so pass a stable key when talking to one solver repeatedly.
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

    private readonly Uri _relay;
    private readonly byte[] _solverPubkey;
    private readonly Key? _identity;
    private readonly TimeSpan _timeout;
    private readonly ILogger<NostrRfqTransport>? _logger;

    /// <summary>Creates the transport.</summary>
    /// <param name="relay">The relay to dial, e.g. <c>wss://relay.example</c>.</param>
    /// <param name="solverPubkey">The solver's x-only key, hex — its address on the relay.</param>
    /// <param name="identity">
    /// Our own key. Omit for a fresh one per negotiation, which is the more private default.
    /// </param>
    /// <param name="timeout">How long to wait for a reply before giving up. Defaults to 30s.</param>
    /// <param name="logger">Optional logger; relay-level faults are reported here.</param>
    public NostrRfqTransport(
        Uri relay,
        string solverPubkey,
        Key? identity = null,
        TimeSpan? timeout = null,
        ILogger<NostrRfqTransport>? logger = null)
    {
        _relay = relay;
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

    /// <inheritdoc />
    public async Task<RfqQuote<TQuoteProfile>> RequestQuoteAsync<TRequestProfile, TQuoteProfile>(
        RfqRequest<TRequestProfile> request,
        CancellationToken cancellationToken = default)
    {
        var reply = await ExchangeAsync(
            JsonSerializer.Serialize(request, RfqProtocol.Json), cancellationToken);

        return RfqProtocol.ExpectQuote<TQuoteProfile>(reply, request.RfqId);
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

    /// <summary>
    /// Publish one sealed payload to the solver and wait for the one addressed back.
    /// </summary>
    private async Task<JsonObject> ExchangeAsync(string payload, CancellationToken cancellationToken)
    {
        var identity = _identity ?? new Key();
        var ourPubkey = NostrEventFactory.Sign(identity, DirectedKind, "x").Pubkey;
        var conversationKey = Nip44.GetConversationKey(identity, _solverPubkey);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_timeout);
        var ct = linked.Token;

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(_relay, ct);

        // Subscribe BEFORE publishing. These kinds are ephemeral, so a relay stores nothing and
        // there is no backlog to fall back on: a reply published while we are not yet listening is
        // simply gone. `since` is floored to whole seconds because that is all `created_at` has, and
        // a second of slack costs less than missing a reply minted in the second we subscribed.
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

        var sealedContent = Nip44.Encrypt(payload, conversationKey, RandomNumberGenerator.GetBytes(32));
        var ev = NostrEventFactory.Sign(
            identity, DirectedKind, sealedContent,
            [["p", Convert.ToHexString(_solverPubkey).ToLowerInvariant()]]);

        await SendAsync(socket, new JsonArray(
            "EVENT", JsonSerializer.SerializeToNode(ev, NostrEventFactory.Json)), ct);

        try
        {
            return await ReadReplyAsync(socket, ev.Id, conversationKey, ct);
        }
        finally
        {
            await CloseQuietlyAsync(socket);
        }
    }

    private async Task<JsonObject> ReadReplyAsync(
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

                // The relay tore down a subscription we still believe is live.
                case "CLOSED" when message.Count >= 2 && message[1]?.GetValue<string>() == publishedId:
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

        throw new NostrRelayException(
            $"no reply from the solver within {_timeout.TotalSeconds:0}s — it may not be connected to {_relay}");
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
    public void Dispose() => _identity?.Dispose();
}
