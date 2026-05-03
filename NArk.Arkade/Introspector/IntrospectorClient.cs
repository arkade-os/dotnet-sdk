using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NArk.Core.Models;

namespace NArk.Arkade.Introspector;

/// <summary>
/// HTTP/JSON implementation of <see cref="IIntrospectorProvider"/>.
/// Designed for DI — register via <c>services.AddIntrospectorClient(...)</c>
/// and inject. The <see cref="HttpClient"/> base address must be set to the
/// introspector server root (e.g. <c>http://localhost:7073</c>).
/// </summary>
public sealed class IntrospectorClient : IIntrospectorProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public IntrospectorClient(HttpClient http, IOptions<IntrospectorClientOptions> options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        var url = options.Value.ServerUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("IntrospectorClientOptions.ServerUrl is required.", nameof(options));
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/", UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<IntrospectorInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var resp = await _http.GetAsync("v1/info", cancellationToken);
        await EnsureSuccessAsync(resp, "info", cancellationToken);
        var body = await resp.Content.ReadFromJsonAsync<InfoResponse>(JsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("Empty introspector info response");
        if (string.IsNullOrEmpty(body.SignerPubkey))
            throw new InvalidOperationException("Invalid introspector info response: missing signerPubkey");
        return new IntrospectorInfo(body.Version ?? "", body.SignerPubkey);
    }

    /// <inheritdoc />
    public async Task<IntrospectorSubmitTxResult> SubmitTxAsync(
        string arkTx, IReadOnlyList<string> checkpointTxs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arkTx);
        ArgumentNullException.ThrowIfNull(checkpointTxs);

        var resp = await _http.PostAsJsonAsync(
            "v1/tx", new SubmitTxRequest(arkTx, checkpointTxs), JsonOptions, cancellationToken);
        await EnsureSuccessAsync(resp, "tx", cancellationToken);
        var body = await resp.Content.ReadFromJsonAsync<SubmitTxResponse>(JsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("Empty submitTx response");
        if (string.IsNullOrEmpty(body.SignedArkTx))
            throw new InvalidOperationException("Invalid submitTx response: missing signedArkTx");
        return new IntrospectorSubmitTxResult(
            body.SignedArkTx,
            body.SignedCheckpointTxs ?? Array.Empty<string>());
    }

    /// <inheritdoc />
    public async Task<string> SubmitIntentAsync(
        string proof, Messages.RegisterIntentMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(message);

        // The introspector's wire shape carries `message` as a JSON-serialized
        // string, not a nested object — matches the ts-sdk's JSON.stringify(message).
        var payload = new SubmitIntentRequest(
            new IntentEnvelope(proof, JsonSerializer.Serialize(message, JsonOptions)));

        var resp = await _http.PostAsJsonAsync("v1/intent", payload, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(resp, "intent", cancellationToken);
        var body = await resp.Content.ReadFromJsonAsync<SubmitIntentResponse>(JsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("Empty submitIntent response");
        if (string.IsNullOrEmpty(body.SignedProof))
            throw new InvalidOperationException("Invalid submitIntent response: missing signedProof");
        return body.SignedProof;
    }

    /// <inheritdoc />
    public async Task<IntrospectorFinalizationResult> SubmitFinalizationAsync(
        string signedProof,
        Messages.RegisterIntentMessage message,
        IReadOnlyList<string> forfeits,
        IReadOnlyList<ConnectorTreeNode>? connectorTree,
        string commitmentTx,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signedProof);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(forfeits);
        ArgumentNullException.ThrowIfNull(commitmentTx);

        var payload = new SubmitFinalizationRequest(
            new IntentEnvelope(signedProof, JsonSerializer.Serialize(message, JsonOptions)),
            forfeits,
            connectorTree,
            commitmentTx);

        var resp = await _http.PostAsJsonAsync("v1/finalization", payload, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(resp, "finalization", cancellationToken);
        var body = await resp.Content.ReadFromJsonAsync<SubmitFinalizationResponse>(JsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("Empty submitFinalization response");
        return new IntrospectorFinalizationResult(
            body.SignedForfeits ?? Array.Empty<string>(),
            body.SignedCommitmentTx);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string op, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Introspector {op} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} — {body}",
            null,
            resp.StatusCode);
    }

    // ─── DTOs (private — public surface is on IIntrospectorProvider) ────────

    private sealed record InfoResponse(string? Version, string SignerPubkey);
    private sealed record SubmitTxRequest(string ArkTx, IReadOnlyList<string> CheckpointTxs);
    private sealed record SubmitTxResponse(string SignedArkTx, IReadOnlyList<string>? SignedCheckpointTxs);
    private sealed record IntentEnvelope(string Proof, string Message);
    private sealed record SubmitIntentRequest(IntentEnvelope Intent);
    private sealed record SubmitIntentResponse(string SignedProof);

    private sealed record SubmitFinalizationRequest(
        IntentEnvelope SignedIntent,
        IReadOnlyList<string> Forfeits,
        IReadOnlyList<ConnectorTreeNode>? ConnectorTree,
        string CommitmentTx);

    private sealed record SubmitFinalizationResponse(
        IReadOnlyList<string>? SignedForfeits,
        string? SignedCommitmentTx);
}

/// <summary>Configuration for <see cref="IntrospectorClient"/>.</summary>
public sealed class IntrospectorClientOptions
{
    /// <summary>Base URL of the introspector server (e.g. <c>http://localhost:7073</c>).</summary>
    public string ServerUrl { get; set; } = string.Empty;
}
