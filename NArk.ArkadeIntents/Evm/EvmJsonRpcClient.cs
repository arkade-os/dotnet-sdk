using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;

namespace NArk.ArkadeIntents.Evm;

/// <summary>Receipt polling policy for the HTTP EVM client.</summary>
public sealed class EvmJsonRpcOptions
{
    /// <summary>Positive delay between receipt queries; defaults to one second.</summary>
    public TimeSpan ReceiptPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Positive maximum time spent waiting for a receipt; defaults to two minutes.</summary>
    public TimeSpan ReceiptTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Maximum response bytes from 256 through 16 MiB; defaults to 1 MiB.</summary>
    public int MaxResponseBytes { get; init; } = 1_048_576;
    /// <summary>Maximum JSON depth from 2 through 128; defaults to 32.</summary>
    public int MaxJsonDepth { get; init; } = 32;

    internal void Validate()
    {
        if (ReceiptPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ReceiptPollInterval));
        if (ReceiptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ReceiptTimeout));
        if (MaxResponseBytes is < 256 or > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        if (MaxJsonDepth is < 2 or > 128)
            throw new ArgumentOutOfRangeException(nameof(MaxJsonDepth));
    }
}

/// <summary>An error returned by an EVM JSON-RPC node.</summary>
/// <param name="code">Node-defined error code.</param><param name="operation">SDK operation name.</param>
public sealed class EvmJsonRpcException(int code, string operation)
    : Exception($"EVM JSON-RPC operation {operation} failed with code {code}.")
{
    /// <summary>Node-defined JSON-RPC error code.</summary>
    public int Code { get; } = code;
}

/// <summary>Production HTTP JSON-RPC implementation of the EVM read and receipt seam.</summary>
public sealed class EvmJsonRpcClient : IEvmSwapRpc
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly EvmJsonRpcOptions _options;
    private readonly TimeProvider _time;
    private long _nextId;

    /// <summary>Creates a client over an externally managed HTTP connection pool.</summary>
    /// <param name="http">Externally managed HTTP client.</param>
    /// <param name="endpoint">Absolute HTTP endpoint, or the client's base address when omitted.</param>
    /// <param name="options">Receipt and untrusted-response limits.</param>
    /// <param name="timeProvider">Clock used for receipt polling.</param>
    public EvmJsonRpcClient(
        HttpClient http,
        Uri? endpoint = null,
        EvmJsonRpcOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _endpoint = endpoint ?? http.BaseAddress
            ?? throw new ArgumentException("supply an EVM endpoint or HttpClient.BaseAddress", nameof(endpoint));
        if (!_endpoint.IsAbsoluteUri || _endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("EVM endpoint must be an absolute HTTP or HTTPS URI", nameof(endpoint));
        _options = options ?? new EvmJsonRpcOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<BigInteger> GetChainIdAsync(CancellationToken cancellationToken = default) =>
        Quantity(await RequestAsync("eth_chainId", [], cancellationToken), "chain id");

    /// <inheritdoc />
    public async Task<BigInteger> GetBlockNumberAsync(CancellationToken cancellationToken = default) =>
        Quantity(await RequestAsync("eth_blockNumber", [], cancellationToken), "block number");

    /// <inheritdoc />
    public async Task<long> GetBlockTimestampAsync(
        BigInteger blockNumber,
        CancellationToken cancellationToken = default)
    {
        if (blockNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(blockNumber));
        var block = Object(await RequestAsync(
            "eth_getBlockByNumber", [Hex(blockNumber), false], cancellationToken), "block");
        var timestamp = Quantity(block["timestamp"], "block timestamp");
        return timestamp <= long.MaxValue ? (long)timestamp
            : throw new EvmJsonRpcException(-1, "block timestamp exceeds Int64");
    }

    /// <inheritdoc />
    public async Task<byte[]> CallAsync(
        string to,
        byte[] data,
        BigInteger? blockNumber = null,
        CancellationToken cancellationToken = default)
    {
        var address = EvmWire.RequireNonZeroAddress(to, nameof(to), lowerCase: true);
        if (blockNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(blockNumber));
        var result = await RequestAsync("eth_call",
            [new JsonObject { ["to"] = address, ["data"] = Data(data) },
             blockNumber is null ? "latest" : Hex(blockNumber.Value)], cancellationToken);
        return Bytes(result, "eth_call result");
    }

    /// <inheritdoc />
    public async Task<EvmTransactionReceipt> WaitForReceiptAsync(
        string transactionHash,
        CancellationToken cancellationToken = default)
    {
        RequireHash(transactionHash, nameof(transactionHash));
        using var timeout = new CancellationTokenSource(_options.ReceiptTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            while (true)
            {
                var node = await RequestAsync(
                    "eth_getTransactionReceipt", [transactionHash], linked.Token, allowNull: true);
                if (node is not null)
                    return Receipt(Object(node, "transaction receipt"));
                await Task.Delay(_options.ReceiptPollInterval, _time, linked.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"EVM receipt was not mined within {_options.ReceiptTimeout}.");
        }
    }

    internal async Task<BigInteger> GetPendingNonceAsync(string address, CancellationToken cancellationToken) =>
        Quantity(await RequestAsync("eth_getTransactionCount",
            [EvmWire.RequireNonZeroAddress(address, nameof(address), lowerCase: true), "pending"], cancellationToken),
            "pending nonce");

    internal async Task<(BigInteger BaseFee, BigInteger PriorityFee)> GetFeeQuoteAsync(
        CancellationToken cancellationToken)
    {
        var priority = Quantity(await RequestAsync("eth_maxPriorityFeePerGas", [], cancellationToken),
            "priority fee");
        var block = Object(await RequestAsync("eth_getBlockByNumber", ["latest", false], cancellationToken),
            "latest block");
        return (Quantity(block["baseFeePerGas"], "base fee"), priority);
    }

    internal async Task<BigInteger> EstimateGasAsync(
        string from,
        string to,
        byte[] data,
        BigInteger maxFee,
        BigInteger priorityFee,
        CancellationToken cancellationToken) => Quantity(await RequestAsync("eth_estimateGas",
        [TransactionObject(from, to, data, maxFee, priorityFee)], cancellationToken), "gas estimate");

    internal async Task<string> SendRawTransactionAsync(string raw, CancellationToken cancellationToken)
    {
        if (!raw.StartsWith("0x02", StringComparison.Ordinal) || !IsEvenHex(raw[2..]))
            throw new ArgumentException("expected a type-2 raw transaction", nameof(raw));
        var result = Text(await RequestAsync("eth_sendRawTransaction", [raw], cancellationToken),
            "transaction hash");
        return RequireHash(result, "transaction hash");
    }

    private async Task<JsonNode?> RequestAsync(
        string method,
        JsonArray parameters,
        CancellationToken cancellationToken,
        bool allowNull = false)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await ReadBoundedAsync(response.Content, method, cancellationToken);
        JsonObject envelope;
        try
        {
            envelope = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions
            {
                MaxDepth = _options.MaxJsonDepth,
            }) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new EvmJsonRpcException(-1, method);
        }
        if (envelope["error"] is JsonObject error)
        {
            var code = error["code"] is JsonValue codeValue && codeValue.TryGetValue<int>(out var parsedCode)
                ? parsedCode : -1;
            throw new EvmJsonRpcException(code, method);
        }
        if (!response.IsSuccessStatusCode)
            throw new EvmJsonRpcException(-1, method);
        if (envelope["jsonrpc"] is not JsonValue version
            || !version.TryGetValue<string>(out var protocol) || protocol != "2.0"
            || envelope["id"] is not JsonValue responseId
            || !responseId.TryGetValue<long>(out var parsedId) || parsedId != id
            || !envelope.ContainsKey("result"))
            throw new EvmJsonRpcException(-1, $"{method} returned an invalid response envelope");
        var result = envelope["result"];
        if (result is null && !allowNull)
            throw new EvmJsonRpcException(-1, $"{method} returned null");
        return result;
    }

    private async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        string method,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaxResponseBytes)
            throw new EvmJsonRpcException(-1, method);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(8_192, _options.MaxResponseBytes + 1)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > _options.MaxResponseBytes)
                throw new EvmJsonRpcException(-1, method);
            output.Write(buffer, 0, read);
        }
    }

    private static EvmTransactionReceipt Receipt(JsonObject value)
    {
        var hash = RequireHash(Text(value["transactionHash"], "receipt transaction hash"),
            "receipt transaction hash");
        var status = Quantity(value["status"], "receipt status");
        if (status != BigInteger.Zero && status != BigInteger.One)
            throw new EvmJsonRpcException(-1, "receipt status is not zero or one");
        var logs = value["logs"] as JsonArray ?? throw new EvmJsonRpcException(-1, "receipt has no logs");
        return new EvmTransactionReceipt(hash, status == 1, logs.Select(Log).ToArray());
    }

    private static EvmLog Log(JsonNode? node)
    {
        var value = Object(node, "receipt log");
        var address = EvmWire.RequireNonZeroAddress(Text(value["address"], "log address"), "log address")
            .ToLowerInvariant();
        var topics = (value["topics"] as JsonArray)?.Select(topic =>
            RequireHash(Text(topic, "log topic"), "log topic")).ToArray()
            ?? throw new EvmJsonRpcException(-1, "log has no topics");
        var data = Text(value["data"], "log data");
        if (!data.StartsWith("0x", StringComparison.Ordinal) || !IsEvenHex(data[2..]))
            throw new EvmJsonRpcException(-1, "log data is not even-length hex");
        return new EvmLog(address, topics, data.ToLowerInvariant());
    }

    private static JsonObject TransactionObject(
        string from, string to, byte[] data, BigInteger maxFee, BigInteger priorityFee) => new()
    {
        ["from"] = EvmWire.RequireNonZeroAddress(from, nameof(from), lowerCase: true),
        ["to"] = EvmWire.RequireNonZeroAddress(to, nameof(to), lowerCase: true),
        ["data"] = Data(data), ["value"] = "0x0",
        ["maxFeePerGas"] = Hex(maxFee), ["maxPriorityFeePerGas"] = Hex(priorityFee),
    };

    private static JsonObject Object(JsonNode? node, string name) => node as JsonObject
        ?? throw new EvmJsonRpcException(-1, $"{name} is not an object");
    private static string Text(JsonNode? node, string name) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text
            : throw new EvmJsonRpcException(-1, $"{name} is not a string");
    private static BigInteger Quantity(JsonNode? node, string name) => Quantity(Text(node, name), name);
    private static BigInteger Quantity(string value, string name)
    {
        if (!value.StartsWith("0x", StringComparison.Ordinal) || value.Length is < 3 or > 66
            || value[2..] != "0" && (value[2] == '0' || !value[2..].All(IsLowerHex)))
            throw new EvmJsonRpcException(-1, $"{name} is not a canonical hex quantity");
        return BigInteger.Parse("0" + value[2..], NumberStyles.AllowHexSpecifier);
    }
    private static byte[] Bytes(JsonNode? node, string name)
    {
        var value = Text(node, name);
        if (!value.StartsWith("0x", StringComparison.Ordinal) || !IsEvenHex(value[2..]))
            throw new EvmJsonRpcException(-1, $"{name} is not even-length hex");
        return Convert.FromHexString(value[2..]);
    }
    private static string RequireHash(string value, string name)
    {
        if (value.Length != 66 || !value.StartsWith("0x", StringComparison.Ordinal)
            || !value[2..].All(IsLowerHex))
            throw new EvmJsonRpcException(-1, $"{name} is not a canonical 32-byte hash");
        return value;
    }
    private static string Data(byte[] value) => "0x" + Convert.ToHexString(value).ToLowerInvariant();
    private static string Hex(BigInteger value) => value >= 0 ? "0x" + value.ToString("x")
        : throw new ArgumentOutOfRangeException(nameof(value));
    private static bool IsEvenHex(string value) => value.Length % 2 == 0 && value.All(IsLowerHex);
    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
