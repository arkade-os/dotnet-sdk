using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Nostr;

/// <summary>A signed Nostr event (NIP-01).</summary>
public sealed class NostrEvent
{
    /// <summary>SHA-256 of the canonical serialization, hex.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The author's x-only key, hex.</summary>
    [JsonPropertyName("pubkey")] public string Pubkey { get; init; } = "";

    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("created_at")] public long CreatedAt { get; init; }

    /// <summary>The event kind.</summary>
    [JsonPropertyName("kind")] public int Kind { get; init; }

    /// <summary>Tags, each a list whose first element names it.</summary>
    [JsonPropertyName("tags")] public List<List<string>> Tags { get; init; } = [];

    /// <summary>The payload. Sealed with NIP-44 on directed kinds.</summary>
    [JsonPropertyName("content")] public string Content { get; init; } = "";

    /// <summary>BIP340 signature over <see cref="Id"/>, hex.</summary>
    [JsonPropertyName("sig")] public string Sig { get; init; } = "";

    /// <summary>The first value of the first tag with this name, or null.</summary>
    /// <param name="name">The tag name, e.g. <c>"p"</c>.</param>
    /// <returns>The tag's first value, or <c>null</c> when it is absent.</returns>
    public string? FirstTag(string name) =>
        Tags.FirstOrDefault(t => t.Count >= 2 && t[0] == name)?[1];
}

/// <summary>Builds and checks NIP-01 events.</summary>
/// <remarks>
/// The signature is what makes a quote non-repudiable — a client can later prove the solver
/// committed to those terms — so it is worth being exact about what gets signed. Every inbound event
/// is verified before anything reads its content, because on a shared relay anyone can publish
/// anything with any claimed author.
/// </remarks>
public static class NostrEventFactory
{
    /// <summary>Sign an event into its final form.</summary>
    /// <param name="key">The author's key.</param>
    /// <param name="kind">The event kind.</param>
    /// <param name="content">The payload, already sealed if the kind calls for it.</param>
    /// <param name="tags">The event's tags.</param>
    /// <param name="createdAt">Unix seconds; defaults to now.</param>
    /// <returns>The signed event.</returns>
    public static NostrEvent Sign(
        Key key, int kind, string content, List<List<string>>? tags = null, long? createdAt = null)
    {
        var priv = ECPrivKey.Create(key.ToBytes());
        var pubkey = Convert.ToHexString(priv.CreateXOnlyPubKey().ToBytes()).ToLowerInvariant();
        var at = createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var eventTags = tags ?? [];

        var id = ComputeId(pubkey, at, kind, eventTags, content);

        if (!priv.TrySignBIP340(Convert.FromHexString(id), null, out var signature) ||
            signature is null)
        {
            throw new InvalidOperationException("failed to sign the event");
        }

        return new NostrEvent
        {
            Id = id,
            Pubkey = pubkey,
            CreatedAt = at,
            Kind = kind,
            Tags = eventTags,
            Content = content,
            Sig = Convert.ToHexString(signature.ToBytes()).ToLowerInvariant(),
        };
    }

    /// <summary>
    /// Check an event's id and signature.
    /// </summary>
    /// <param name="ev">The event as received.</param>
    /// <returns><c>true</c> when the id matches the content and the signature matches the id.</returns>
    /// <remarks>
    /// Both halves matter. A valid signature over a mismatched id would let an author's real
    /// signature be replayed onto different content.
    /// </remarks>
    public static bool Verify(NostrEvent ev)
    {
        try
        {
            if (ComputeId(ev.Pubkey, ev.CreatedAt, ev.Kind, ev.Tags, ev.Content) != ev.Id) return false;

            var pubkey = ECXOnlyPubKey.Create(Convert.FromHexString(ev.Pubkey));
            return SecpSchnorrSignature.TryCreate(Convert.FromHexString(ev.Sig), out var sig) &&
                   sig is not null &&
                   pubkey.SigVerifyBIP340(sig, Convert.FromHexString(ev.Id));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The canonical serialization an event's id is the hash of:
    /// <c>[0, pubkey, created_at, kind, tags, content]</c>, no whitespace.
    /// </summary>
    /// <remarks>
    /// Hand-built rather than handed to a serializer. NIP-01 fixes an exact escaping set — only
    /// <c>\n \" \\ \r \t \b \f</c> are escaped and everything else passes through literally — while
    /// general-purpose JSON writers escape more (non-ASCII, <c>&lt;</c>, <c>&amp;</c>) or less. Any
    /// difference changes the hash, and therefore the id, and therefore every signature check.
    /// </remarks>
    internal static string ComputeId(
        string pubkey, long createdAt, int kind, List<List<string>> tags, string content)
    {
        var sb = new StringBuilder();
        sb.Append("[0,\"").Append(pubkey).Append("\",").Append(createdAt).Append(',').Append(kind).Append(",[");
        for (var i = 0; i < tags.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[');
            for (var j = 0; j < tags[i].Count; j++)
            {
                if (j > 0) sb.Append(',');
                AppendEscaped(sb, tags[i][j]);
            }
            sb.Append(']');
        }
        sb.Append("],");
        AppendEscaped(sb, content);
        sb.Append(']');

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendEscaped(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Serializer settings for events on the wire: no extra escaping, no reordering.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
