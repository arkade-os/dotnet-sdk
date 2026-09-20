using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Helpers;
using NArk.ArkadeIntents.Lightning;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.ArkadeIntents.Covclaim;

/// <summary>
/// <see cref="ICovclaimdClient"/> over covclaimd's REST gateway.
/// </summary>
/// <remarks>
/// Every request is bounded by <see cref="CovclaimdOptions.RequestTimeout"/> and every failure
/// surfaces as <see cref="CovclaimdException"/>, including the ones a bare
/// <see cref="HttpClient"/> would raise as transport noise — callers on this path are meant to
/// catch and carry on, and that is only reasonable if the failures have one shape.
/// </remarks>
public sealed class CovclaimdClient : ICovclaimdClient
{
    private const string KeysPath = "v1/preimage/covclaimd-pubkey";
    private const string RevealPath = "v1/reveal";
    private const int PreimageLength = 32;

    private readonly HttpClient _httpClient;
    private readonly CovclaimdOptions _options;
    private readonly IAesGcmCipher _cipher;
    private readonly ILogger<CovclaimdClient>? _logger;
    private readonly SemaphoreSlim _keysLock = new(1, 1);
    private CovclaimdKeys? _cachedKeys;

    /// <summary>Creates the client.</summary>
    /// <param name="httpClient">Transport. Its base address is set from the options when unset.</param>
    /// <param name="options">Where the daemon is, and how patient to be with it.</param>
    /// <param name="cipher">
    /// Supplies AES-GCM for sealing. Defaults to the platform's, which is right everywhere except a
    /// browser — see <see cref="IAesGcmCipher"/>.
    /// </param>
    /// <param name="logger">Optional log sink.</param>
    public CovclaimdClient(
        HttpClient httpClient,
        IOptions<CovclaimdOptions> options,
        IAesGcmCipher? cipher = null,
        ILogger<CovclaimdClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _options = options.Value;
        _cipher = cipher ?? new AesGcmCipher();
        _logger = logger;

        _httpClient.BaseAddress ??= _options.BaseAddress
            ?? throw new InvalidOperationException(
                $"{nameof(CovclaimdOptions)}.{nameof(CovclaimdOptions.BaseAddress)} must be set.");

        AssertTransportSecure(_httpClient.BaseAddress, _options.AllowInsecureHttp);
    }

    /// <summary>
    /// Refuse a daemon reachable only over plain HTTP, unless it is on this machine or the caller
    /// has said otherwise.
    /// </summary>
    /// <remarks>
    /// This is not transport hygiene, it is the trust anchor. The key served by whatever answers
    /// this address is the key every preimage gets sealed to, so anyone able to answer in its place
    /// substitutes their own and reads the secrets — and on a receive leg a leaked preimage settles
    /// the payer's invoice without this wallet ever claiming. Loopback is exempt because that is
    /// where covclaimd runs by default and nothing crosses a wire to intercept.
    /// </remarks>
    private static void AssertTransportSecure(Uri baseAddress, bool allowInsecureHttp)
    {
        if (baseAddress.Scheme == Uri.UriSchemeHttps || allowInsecureHttp || baseAddress.IsLoopback)
        {
            return;
        }

        throw new InvalidOperationException(
            $"covclaimd must be reached over https (got {baseAddress.Scheme}://{baseAddress.Host}): " +
            "the key it serves is the one every preimage gets sealed to. Set " +
            $"{nameof(CovclaimdOptions)}.{nameof(CovclaimdOptions.AllowInsecureHttp)} to override.");
    }

    /// <inheritdoc />
    public async Task<CovclaimdKeys> GetKeysAsync(CancellationToken cancellationToken = default)
    {
        if (_options.CacheKeys && _cachedKeys is { } cached)
        {
            return cached;
        }

        await _keysLock.WaitAsync(cancellationToken);
        try
        {
            if (_options.CacheKeys && _cachedKeys is { } raced)
            {
                return raced;
            }

            var body = await SendAsync(async ct =>
                {
                    using var response = await _httpClient.GetAsync(KeysPath, ct);
                    await EnsureSuccessAsync(response, "fetch daemon keys", ct);
                    return await response.Content.ReadFromJsonAsync<CovclaimdKeysResponse>(ct);
                }, "fetch daemon keys", cancellationToken)
                ?? throw new CovclaimdException("covclaimd returned an empty key response.");

            var keys = new CovclaimdKeys(
                ParsePubKey(body.CovclaimdPubKey, "covclaimd_pub_key"),
                ParsePubKey(body.EmulatorPubKey, "emulator_pub_key"));

            if (_options.CacheKeys)
            {
                _cachedKeys = keys;
            }
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
        byte[] arkadeScript,
        TapScript[] taptree,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(swapAddress);
        ArgumentNullException.ThrowIfNull(preimage);
        ArgumentNullException.ThrowIfNull(arkadeScript);
        ArgumentNullException.ThrowIfNull(taptree);

        if (preimage.Length != PreimageLength)
        {
            throw new ArgumentException(
                $"a preimage is {PreimageLength} bytes, got {preimage.Length}", nameof(preimage));
        }
        if (arkadeScript.Length == 0)
        {
            throw new ArgumentException("the covenant's claim script is empty", nameof(arkadeScript));
        }
        if (taptree.Length == 0)
        {
            throw new ArgumentException(
                "a taptree with no leaves cannot hash to the address it claims to describe",
                nameof(taptree));
        }

        var keys = await GetKeysAsync(cancellationToken);

        // Only the preimage is sealed. The arkade script travels in the clear because it carries no
        // secret — it says where the claim must pay, which the solver already knows.
        var sealed_ = await ClaimPacket.SealAsync(
            preimage,
            Convert.ToHexString(keys.CovclaimdPubKey.ToBytes(true)).ToLowerInvariant(),
            _cipher,
            cancellationToken);

        var request = new RevealRequestBody
        {
            SwapAddress = swapAddress,
            Packet = new RevealPacketBody
            {
                Ciphertext = sealed_.Packet,
                ArkadeScript = Convert.ToBase64String(arkadeScript),
            },
            Taptree = Convert.ToHexString(PsbtHelpers.EncodeTaprootTree(taptree)).ToLowerInvariant(),
        };

        await SendAsync<object?>(async ct =>
        {
            using var response = await _httpClient.PostAsJsonAsync(RevealPath, request, ct);
            await EnsureSuccessAsync(response, $"register a claim for {swapAddress}", ct);
            return null;
        }, $"register a claim for {swapAddress}", cancellationToken);

        _logger?.LogInformation(
            "covclaimd accepted a claim registration for {SwapAddress} ({LeafCount} taptree leaves)",
            swapAddress, taptree.Length);
    }

    private async Task<T> SendAsync<T>(
        Func<CancellationToken, Task<T>> send, string operation, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.RequestTimeout);
        try
        {
            return await send(timeoutCts.Token);
        }
        // The caller's own cancellation is theirs to see; only our timeout becomes an exception
        // about covclaimd.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new CovclaimdException(
                $"covclaimd did not answer within {_options.RequestTimeout.TotalSeconds:0.#}s " +
                $"while trying to {operation}.", null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CovclaimdException($"covclaimd is unreachable: could not {operation}.", null, ex);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        string? detail = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<CovclaimdErrorBody>(cancellationToken);
            detail = error?.Error;
        }
        catch
        {
            // A gateway that fails without a JSON body still has a status worth reporting, and the
            // status is the half that says whether to retry.
        }

        throw new CovclaimdException(
            $"covclaimd refused to {operation}: HTTP {status}{(detail is null ? "" : $" — {detail}")}",
            status);
    }

    private static ECPubKey ParsePubKey(string? hex, string field)
    {
        if (hex is not { Length: > 0 })
        {
            throw new CovclaimdException($"covclaimd returned no {field}.");
        }
        try
        {
            return ECPubKey.Create(Convert.FromHexString(hex));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new CovclaimdException($"covclaimd returned an unparseable {field}: '{hex}'.", null, ex);
        }
    }
}
