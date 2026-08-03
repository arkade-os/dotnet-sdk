using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Helpers;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Arkade.Covclaim;

/// <summary>
/// REST client for a <c>covclaimd</c> claim daemon, talking to the gateway
/// exposed on <c>COVCLAIMD_HTTP_PORT</c>.
/// </summary>
/// <remarks>
/// Uses the reveal path (<c>POST /v1/reveal</c>) rather than stamping claim
/// packets into the funding transaction's Arkade extension. That is the only
/// option whenever a counterparty builds the funding transaction — as Boltz does
/// for reverse and BTC→Arkade swaps — since we never get to add an output to it.
/// </remarks>
public sealed class CovclaimdClient : ICovclaimdClient
{
    private const string KeysPath = "v1/preimage/covclaimd-pubkey";
    private const string RevealPath = "v1/reveal";
    private const int PreimageLength = 32;

    private readonly HttpClient _httpClient;
    private readonly CovclaimdOptions _options;
    private readonly ILogger<CovclaimdClient>? _logger;
    private readonly SemaphoreSlim _keysLock = new(1, 1);
    private CovclaimdKeys? _cachedKeys;

    /// <summary>
    /// Creates a client over <paramref name="httpClient"/>. The base address is
    /// taken from <paramref name="options"/> when the HTTP client has none of its own.
    /// </summary>
    public CovclaimdClient(
        HttpClient httpClient,
        IOptions<CovclaimdOptions> options,
        ILogger<CovclaimdClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress ??= _options.BaseAddress
            ?? throw new InvalidOperationException(
                $"{nameof(CovclaimdOptions)}.{nameof(CovclaimdOptions.BaseAddress)} must be set.");
    }

    /// <summary>
    /// Runs <paramref name="send"/> under
    /// <see cref="CovclaimdOptions.RequestTimeout"/>, translating transport failures into
    /// <see cref="CovclaimdException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bound is applied per request rather than by setting
    /// <see cref="HttpClient.Timeout"/>, because that property throws once the client has
    /// issued a request and the instance may be shared — a client this type does not own
    /// is not ours to reconfigure. Claim registration runs inline while a swap is being
    /// created, so the bound matters: without it an unreachable daemon would stall swap
    /// creation for HttpClient's 100 second default.
    /// </para>
    /// <para>
    /// A cancellation coming from <paramref name="cancellationToken"/> is rethrown as-is;
    /// only our own timeout is reported as a failure, so callers can still distinguish
    /// "the caller gave up" from "the daemon did not answer".
    /// </para>
    /// </remarks>
    private async Task<T> SendAsync<T>(
        Func<CancellationToken, Task<T>> send, string operation, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.RequestTimeout);

        try
        {
            return await send(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new CovclaimdException(
                $"covclaimd did not respond within {_options.RequestTimeout.TotalSeconds:0.#}s " +
                $"while trying to {operation}.", null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CovclaimdException($"covclaimd is unreachable: could not {operation}.", null, ex);
        }
    }

    /// <inheritdoc />
    public async Task<CovclaimdKeys> GetKeysAsync(CancellationToken cancellationToken = default)
    {
        if (_options.CacheKeys && _cachedKeys is { } cached)
            return cached;

        await _keysLock.WaitAsync(cancellationToken);
        try
        {
            if (_options.CacheKeys && _cachedKeys is { } raced)
                return raced;

            var body = await SendAsync(async ct =>
            {
                using var response = await _httpClient.GetAsync(KeysPath, ct);
                await EnsureSuccessAsync(response, "fetch daemon keys", ct);
                return await response.Content.ReadFromJsonAsync<CovclaimdKeysResponse>(ct);
            }, "fetch daemon keys", cancellationToken)
                ?? throw new CovclaimdException("Daemon returned an empty key response.");

            var keys = new CovclaimdKeys(
                ParsePubKey(body.CovclaimdPubKey, "covclaimd_pub_key"),
                ParsePubKey(body.EmulatorPubKey, "emulator_pub_key"));

            if (_options.CacheKeys)
                _cachedKeys = keys;

            return keys;
        }
        finally
        {
            _keysLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RevealAsync(
        string swapAddress,
        byte[] preimage,
        Script claimDestination,
        TapScript[] taptree,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(swapAddress);
        ArgumentNullException.ThrowIfNull(preimage);
        ArgumentNullException.ThrowIfNull(claimDestination);
        ArgumentNullException.ThrowIfNull(taptree);

        if (preimage.Length != PreimageLength)
            throw new ArgumentException(
                $"Preimage must be {PreimageLength} bytes, got {preimage.Length}.", nameof(preimage));
        if (taptree.Length == 0)
            throw new ArgumentException("Taptree must not be empty.", nameof(taptree));

        var keys = await GetKeysAsync(cancellationToken);

        // The Arkade script travels in the clear: it commits the claim to our
        // destination and carries no secret. Only the preimage is encrypted.
        var arkadeScript = CovenantClaimScript.EnforcePayTo(claimDestination);
        var ciphertext = CovclaimEcies.Encrypt(keys.CovclaimdPubKey, preimage);

        var request = new RevealRequestBody
        {
            SwapAddress = swapAddress,
            Packet = new RevealPacketBody
            {
                Ciphertext = Convert.ToBase64String(ciphertext),
                ArkadeScript = Convert.ToBase64String(arkadeScript),
            },
            Taptree = Convert.ToHexString(PsbtHelpers.EncodeTaprootTree(taptree)).ToLowerInvariant(),
        };

        await SendAsync<object?>(async ct =>
        {
            using var response = await _httpClient.PostAsJsonAsync(RevealPath, request, ct);
            await EnsureSuccessAsync(response, $"register claim for {swapAddress}", ct);
            return null;
        }, $"register claim for {swapAddress}", cancellationToken);

        _logger?.LogInformation(
            "Registered covclaimd claim for swap address {SwapAddress} ({LeafCount} taptree leaves)",
            swapAddress, taptree.Length);
    }

    /// <summary>
    /// Throws a <see cref="CovclaimdException"/> carrying the daemon's own error
    /// message when the response is not a success.
    /// </summary>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var status = (int)response.StatusCode;
        string? detail = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<CovclaimdErrorBody>(cancellationToken);
            detail = error?.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Non-JSON body (e.g. a proxy error page) — the status code is all we have.
        }

        throw new CovclaimdException(
            $"covclaimd failed to {operation}: HTTP {status}{(detail is null ? "" : $" — {detail}")}",
            status);
    }

    private static ECPubKey ParsePubKey(string? hex, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(hex))
            throw new CovclaimdException($"Daemon response is missing '{fieldName}'.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException ex)
        {
            throw new CovclaimdException($"Daemon returned a malformed '{fieldName}': not hex.", null, ex);
        }

        if (!ECPubKey.TryCreate(bytes, Context.Instance, out _, out var pubKey))
            throw new CovclaimdException($"Daemon returned an unparseable '{fieldName}'.");

        return pubKey;
    }
}
