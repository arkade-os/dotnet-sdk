using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.WebSocket;

namespace NArk.Tests;

[TestFixture]
public class BoltzWebsocketClientTests
{
    private static readonly Uri WebsocketUri = new("wss://example.test/v2/ws");

    [Test]
    public async Task UnacknowledgedRequestDropsConnection()
    {
        var socket = new FakeWebSocket();
        await using var client = CreateClient(socket, TimeSpan.FromMilliseconds(30));
        await client.ConnectAsync();
        var disconnected = client.WaitUntilDisconnected(CancellationToken.None);

        var exception = Assert.ThrowsAsync<WebSocketException>(
            async () => await client.SubscribeAsync(["swap-1"]));

        Assert.That(exception!.Message, Does.Contain("timed out"));
        await disconnected.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(socket.State, Is.EqualTo(WebSocketState.Aborted));
    }

    [Test]
    public async Task CancelledRequestIsNotSentAndLeavesClientUsable()
    {
        var socket = new FakeWebSocket();
        // Subscribe is deliberately left unacknowledged so the first call stays in flight.
        socket.AcknowledgeAutomatically("unsubscribe");

        await using var client = CreateClient(socket, TimeSpan.FromSeconds(1));
        await client.ConnectAsync();

        var first = client.SubscribeAsync(["swap-1"]);
        await WaitUntil(() => socket.Sent.Count == 1);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(
            async () => await client.SubscribeAsync(["swap-2"], cancelled.Token));

        socket.Queue(FakeWebSocket.Acknowledgement("subscribe"));
        await first;
        await client.UnsubscribeAsync(["swap-1"]);

        Assert.That(socket.Sent.Select(Operation), Is.EqualTo(["subscribe", "unsubscribe"]));
    }

    [Test]
    public async Task ReceiveFailureSignalsDisconnection()
    {
        var socket = new FakeWebSocket();
        await using var client = CreateClient(socket, TimeSpan.FromSeconds(1));
        await client.ConnectAsync();
        var disconnected = client.WaitUntilDisconnected(CancellationToken.None);

        socket.FailReceive(new WebSocketException("connection reset"));

        await disconnected.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(socket.State, Is.EqualTo(WebSocketState.Aborted));
    }

    [Test]
    public async Task SilentConnectionIsDroppedWhenPongStops()
    {
        var socket = new FakeWebSocket();
        await using var client = CreateClient(
            socket,
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(10));
        await client.ConnectAsync();
        var disconnected = client.WaitUntilDisconnected(CancellationToken.None);

        await disconnected.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.That(socket.Sent.Select(Operation), Does.Contain("ping"));
        Assert.That(socket.State, Is.EqualTo(WebSocketState.Aborted));
    }

    [Test]
    public async Task PongKeepsConnectionAlive()
    {
        var socket = new FakeWebSocket();
        socket.AcknowledgeAutomatically("ping");

        await using var client = CreateClient(
            socket,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(10));
        await client.ConnectAsync();

        await WaitUntil(() => socket.Sent.Count(message => Operation(message) == "ping") >= 3);

        Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
    }

    private static BoltzWebsocketClient CreateClient(
        FakeWebSocket socket,
        TimeSpan timeout,
        TimeSpan? heartbeat = null) =>
        new(WebsocketUri, timeout, heartbeat, (_, _) => Task.FromResult<WebSocket>(socket));

    private static string? Operation(string message) =>
        JsonDocument.Parse(message).RootElement.GetProperty("op").GetString();

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private int _state = (int)WebSocketState.Open;

        private Action<string>? _onSend;

        public ConcurrentQueue<string> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
        public override string? SubProtocol => null;

        public void Queue(string json) =>
            _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

        public void FailReceive(Exception exception) =>
            _incoming.Writer.TryComplete(exception);

        /// <summary>
        /// Replies to <paramref name="operations"/> with the acknowledgement Boltz
        /// sends for them. Operations left out stay unacknowledged, which is how the
        /// tests drive the client's request-timeout path.
        /// </summary>
        public void AcknowledgeAutomatically(params string[] operations) =>
            _onSend = message =>
            {
                if (Operation(message) is { } operation && operations.Contains(operation))
                    Queue(Acknowledgement(operation));
            };

        public static string Acknowledgement(string operation) =>
            operation == "ping"
                ? """{"event":"pong"}"""
                : $$"""{"event":"{{operation}}","channel":"swap.update","args":[]}""";

        public override void Abort()
        {
            Interlocked.Exchange(ref _state, (int)WebSocketState.Aborted);
            _incoming.Writer.TryComplete();
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _state, (int)WebSocketState.Closed);
            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
            if (State != WebSocketState.Aborted)
                Interlocked.Exchange(ref _state, (int)WebSocketState.Closed);
            _incoming.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            var payload = await _incoming.Reader.ReadAsync(cancellationToken);
            Array.Copy(payload, 0, buffer.Array!, buffer.Offset, payload.Length);
            return new WebSocketReceiveResult(
                payload.Length,
                WebSocketMessageType.Text,
                endOfMessage: true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var message = Encoding.UTF8.GetString(buffer);
            Sent.Enqueue(message);
            _onSend?.Invoke(message);
            return Task.CompletedTask;
        }
    }
}

[TestFixture]
public class BoltzWebsocketReconnectTests
{
    [Test]
    public async Task OwnerReconnectsAndResubscribesAfterDisconnect()
    {
        var provider = BoltzTestFixture.CreateProvider();

        var first = new FakeClient(disconnectImmediately: true);
        var second = new FakeClient(disconnectImmediately: false);
        var clients = new Queue<IBoltzWebsocketClient>([first, second]);
        provider.WebsocketClientFactory = _ => clients.Dequeue();
        provider.WebsocketReconnectDelay = TimeSpan.Zero;
        provider.WatchSwap("swap-1");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var loop = provider.RunWebsocketLoop(cancellation.Token);
        await second.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await cancellation.CancelAsync();
        await loop;
        await provider.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first.Subscriptions.Single(), Is.EqualTo(new[] { "swap-1" }));
            Assert.That(second.Subscriptions.Single(), Is.EqualTo(new[] { "swap-1" }));
            Assert.That(first.Disposed, Is.True);
            Assert.That(second.Disposed, Is.True);
        });
    }

    private sealed class FakeClient(bool disconnectImmediately) : IBoltzWebsocketClient
    {
        public event Func<WebSocketResponse?, Task>? OnAnyEventReceived;
        public List<string[]> Subscriptions { get; } = [];
        public TaskCompletionSource Subscribed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SubscribeAsync(
            string[] swapIds,
            CancellationToken cancellationToken = default)
        {
            Subscriptions.Add(swapIds);
            Subscribed.TrySetResult();
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(
            string[] swapIds,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitUntilDisconnected(CancellationToken cancellationToken) =>
            disconnectImmediately
                ? Task.CompletedTask
                : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            OnAnyEventReceived = null;
            return ValueTask.CompletedTask;
        }
    }
}
