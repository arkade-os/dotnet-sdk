using System.Net.WebSockets;
using System.Text.Json;
using NArk.Swaps.Boltz.Models.WebSocket;

namespace NArk.Swaps.Boltz.Client;

internal interface IBoltzWebsocketClient : IAsyncDisposable
{
    event Func<WebSocketResponse?, Task>? OnAnyEventReceived;
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task SubscribeAsync(string[] swapIds, CancellationToken cancellationToken = default);
    Task UnsubscribeAsync(string[] swapIds, CancellationToken cancellationToken = default);
    Task WaitUntilDisconnected(CancellationToken cancellationToken);
}

/// <summary>
/// Manages WebSocket communication with the Boltz API for one connection.
/// An instance is single-use: once the connection drops, the owner disposes it
/// and creates a new client rather than reconnecting in place.
/// </summary>
public class BoltzWebsocketClient : IBoltzWebsocketClient
{
    private const string SwapUpdateChannel = "swap.update";

    private readonly Uri _webSocketUri;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connect;
    private readonly TimeSpan _requestResponseTimeout;
    private readonly TimeSpan? _heartbeatInterval;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private readonly TaskCompletionSource _disconnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _lifetime;
    private WebSocket? _socket;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private int _invalidated;
    private int _disposed;

    /// <summary>
    /// Occurs for any WebSocket event, providing a common event object.
    /// </summary>
    public event Func<WebSocketResponse?, Task>? OnAnyEventReceived;

    /// <summary>Default time to wait for a subscribe or unsubscribe acknowledgement.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default interval between keepalive pings.</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="BoltzWebsocketClient"/> class
    /// with the default timeouts.
    /// </summary>
    /// <param name="webSocketUri">The explicit URI for the WebSocket connection.</param>
    public BoltzWebsocketClient(Uri webSocketUri)
        : this(webSocketUri, DefaultRequestTimeout, DefaultHeartbeatInterval)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BoltzWebsocketClient"/> class.
    /// </summary>
    /// <param name="webSocketUri">The explicit URI for the WebSocket connection.</param>
    /// <param name="requestResponseTimeout">
    /// How long to wait for an acknowledgement before treating the connection as dead.
    /// </param>
    /// <param name="heartbeatInterval">
    /// Interval between keepalive pings, or <c>null</c> to disable the heartbeat.
    /// </param>
    /// <param name="connect">
    /// Overrides how the underlying socket is opened — for a proxy-configured or
    /// otherwise pre-customised <see cref="ClientWebSocket"/>. Defaults to a plain connect.
    /// </param>
    public BoltzWebsocketClient(
        Uri webSocketUri,
        TimeSpan requestResponseTimeout,
        TimeSpan? heartbeatInterval,
        Func<Uri, CancellationToken, Task<WebSocket>>? connect = null)
    {
        _webSocketUri = webSocketUri ?? throw new ArgumentNullException(nameof(webSocketUri));
        _connect = connect ?? ConnectSocket;
        _requestResponseTimeout = requestResponseTimeout;
        _heartbeatInterval = heartbeatInterval;
    }

    /// <summary>
    /// Creates and connects a new BoltzWebsocketClient instance.
    /// </summary>
    /// <param name="webSocketUri">The WebSocket URI to connect to.</param>
    /// <param name="cancellationToken">Cancellation token for the connection attempt.</param>
    /// <returns>A connected BoltzWebsocketClient instance.</returns>
    public static async Task<BoltzWebsocketClient> CreateAndConnectAsync(
        Uri webSocketUri,
        CancellationToken cancellationToken = default)
    {
        var client = new BoltzWebsocketClient(webSocketUri);
        await client.ConnectAsync(cancellationToken);
        return client;
    }

    private static async Task<WebSocket> ConnectSocket(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Connects to the Boltz WebSocket API. Cancelling <paramref name="cancellationToken"/>
    /// after the connection is established tears the connection down.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token bound to the connection's lifetime.</param>
    /// <exception cref="InvalidOperationException">Thrown if this client has already connected.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _operationSemaphore.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_socket is not null)
                throw new InvalidOperationException(
                    "WebSocket is already connected; create a new client to reconnect.");

            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            WebSocket socket;
            try
            {
                socket = await _connect(_webSocketUri, lifetime.Token);
            }
            catch
            {
                lifetime.Dispose();
                throw;
            }

            _lifetime = lifetime;
            _socket = socket;
            _receiveTask = ReceiveLoopAsync();
            if (_heartbeatInterval is not null)
                _heartbeatTask = HeartbeatLoopAsync();
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    /// <summary>
    /// Subscribes to WebSocket updates for specific swap IDs.
    /// </summary>
    public Task SubscribeAsync(string[] swapIds, CancellationToken cancellationToken = default) =>
        SendChannelOperationAsync("subscribe", swapIds, cancellationToken);

    /// <summary>
    /// Unsubscribes from WebSocket updates for specific swap IDs.
    /// </summary>
    public Task UnsubscribeAsync(string[] swapIds, CancellationToken cancellationToken = default) =>
        SendChannelOperationAsync("unsubscribe", swapIds, cancellationToken);

    private Task SendChannelOperationAsync(
        string operation,
        string[] swapIds,
        CancellationToken cancellationToken) =>
        SendRequestAsync(
            JsonSerializer.SerializeToUtf8Bytes(new WebSocketRequest
            {
                Operation = operation,
                Channel = SwapUpdateChannel,
                Args = JsonSerializer.SerializeToNode(swapIds)!.AsArray(),
            }),
            response => response.Event == operation && response.Channel == SwapUpdateChannel,
            operation,
            cancellationToken);

    private Task PingAsync(CancellationToken cancellationToken) =>
        SendRequestAsync(
            """{"op":"ping"}"""u8.ToArray(),
            response => response is { Event: "pong" },
            "ping",
            cancellationToken);

    private async Task SendRequestAsync(
        byte[] message,
        Func<WebSocketResponse, bool> matchesResponse,
        string operation,
        CancellationToken cancellationToken)
    {
        await _operationSemaphore.WaitAsync(cancellationToken);
        try
        {
            var socket = _socket;
            var lifetime = _lifetime;
            if (socket is null || lifetime is null ||
                Volatile.Read(ref _invalidated) != 0 ||
                socket.State != WebSocketState.Open)
            {
                throw new WebSocketException(WebSocketError.InvalidState, "WebSocket is not connected.");
            }

            var response = new TaskCompletionSource<WebSocketResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Task OnEvent(WebSocketResponse? candidate)
            {
                if (candidate is not null && matchesResponse(candidate))
                    response.TrySetResult(candidate);
                return Task.CompletedTask;
            }

            OnAnyEventReceived += OnEvent;
            await using var registration = lifetime.Token.Register(
                () => response.TrySetCanceled(lifetime.Token));

            try
            {
                await socket.SendAsync(
                    message,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
                await response.Task.WaitAsync(_requestResponseTimeout, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                Invalidate();
                throw new WebSocketException(
                    WebSocketError.ConnectionClosedPrematurely,
                    $"Boltz WebSocket {operation} acknowledgement timed out.",
                    ex);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Invalidate();
                throw new WebSocketException(
                    WebSocketError.ConnectionClosedPrematurely,
                    $"Boltz WebSocket disconnected during {operation}.");
            }
            catch (WebSocketException)
            {
                Invalidate();
                throw;
            }
            finally
            {
                OnAnyEventReceived -= OnEvent;
            }
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        var lifetime = _lifetime!;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval!.Value, lifetime.Token);
                await PingAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            Invalidate();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var socket = _socket!;
        var lifetime = _lifetime!;
        var buffer = new byte[8192];
        try
        {
            while (socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                message.Position = 0;
                try
                {
                    var response = await JsonSerializer.DeserializeAsync<WebSocketResponse>(
                        message,
                        cancellationToken: lifetime.Token);
                    Dispatch(response);
                }
                catch (JsonException)
                {
                    Dispatch(null);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            Dispatch(null);
        }
        finally
        {
            Invalidate();
        }
    }

    private void Dispatch(WebSocketResponse? response)
    {
        if (OnAnyEventReceived is not { } handlers)
            return;

        foreach (Func<WebSocketResponse?, Task> handler in handlers.GetInvocationList())
            _ = Observe(handler, response);
    }

    private static async Task Observe(Func<WebSocketResponse?, Task> handler, WebSocketResponse? response)
    {
        try
        {
            await handler(response);
        }
        catch
        {
            // Event consumers own their diagnostics.
        }
    }

    /// <summary>
    /// Tears the connection down once; subsequent calls are no-ops.
    /// </summary>
    private void Invalidate()
    {
        if (Interlocked.Exchange(ref _invalidated, 1) != 0)
            return;
        Teardown();
    }

    private void Teardown()
    {
        try { _lifetime?.Cancel(); } catch (ObjectDisposedException) { }
        try { _socket?.Abort(); } catch { }
        try { _socket?.Dispose(); } catch { }
        _disconnected.TrySetResult();
    }

    /// <summary>
    /// Waits until the WebSocket connection is disconnected.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the wait operation.</param>
    /// <returns>A task that completes when the WebSocket is disconnected.</returns>
    public async Task WaitUntilDisconnected(CancellationToken cancellationToken)
    {
        if (_socket is null)
            return;
        await _disconnected.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Disposes the client and disconnects the WebSocket.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Break any in-flight request before contending for the semaphore it holds.
        Invalidate();

        await _operationSemaphore.WaitAsync();
        try
        {
            // A ConnectAsync that raced past the disposed check owns the socket now.
            Teardown();
        }
        finally
        {
            _operationSemaphore.Release();
        }

        await IgnoreCancellation(_receiveTask);
        await IgnoreCancellation(_heartbeatTask);
        _lifetime?.Dispose();
        _operationSemaphore.Dispose();
    }

    private static async Task IgnoreCancellation(Task? task)
    {
        if (task is null)
            return;
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
