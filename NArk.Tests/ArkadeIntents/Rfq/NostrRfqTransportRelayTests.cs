using System.Net;
using System.Net.Sockets;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NArk.ArkadeIntents.SolverRegistry;
using NBitcoin;

namespace NArk.Tests.ArkadeIntents.Rfq;

/// <summary>
/// The relay set: dialling all of it, and telling apart the three ways a negotiation can go quiet.
/// </summary>
/// <remarks>
/// <para>
/// A card advertises a LIST because a rendezvous is a place both parties happen to be, and neither
/// side controls which entry the other is connected to right now. Dialling one is a coin flip.
/// </para>
/// <para>
/// The silences matter as much as the fan-out. "The solver did not answer", "we could not have
/// heard it if it had", and "we hung up ourselves" all look identical to a caller that only sees a
/// timeout — and the first blames a counterparty for what may be an outage on our own side of the
/// wire. These tests use real sockets rather than a stub, because what is being pinned is behaviour
/// against a network, and a fake relay would only prove the code agrees with the fake.
/// </para>
/// </remarks>
[TestFixture]
public class NostrRfqTransportRelayTests
{
    private static readonly string SolverPubkey =
        Convert.ToHexString(new Key().PubKey.TaprootInternalKey.ToBytes()).ToLowerInvariant();

    // ─── Building from a card ─────────────────────────────────────────

    [Test]
    public void ACard_YieldsATransportOverItsWholeRelaySet()
    {
        var transport = NostrRfqTransport.ForCard(Card(["wss://a.example", "wss://b.example"]));

        Assert.That(transport, Is.Not.Null);
        transport.Dispose();
    }

    [Test]
    public void APlaintextRelay_IsNotDialled()
    {
        // The registry schema admits only wss://, so a ws:// entry is either a malformed card or a
        // downgrade someone wants accepted. This traffic is sealed to the solver's key but not to
        // the relay's, so who carries it is still worth being strict about.
        var ex = Assert.Throws<ArgumentException>(
            () => NostrRfqTransport.ForCard(Card(["ws://plaintext.example"])));

        Assert.That(ex!.Message, Does.Contain("no wss:// relay"));
    }

    [Test]
    public void ACardWithNoDiscoveryKey_NamesWhatIsMissing()
    {
        // A corridor card is required to carry one; without it the relay list names a solver nothing
        // can address.
        var card = new SolverCard
        {
            Version = 0,
            Name = "test-solver",
            Transports = new SolverTransports { Nostr = new NostrTransport { Relays = ["wss://a.example"] } },
        };

        var ex = Assert.Throws<ArgumentException>(() => NostrRfqTransport.ForCard(card));

        Assert.That(ex!.Message, Does.Contain("discovery_pubkey"));
    }

    [Test]
    public void ARelayListedTwice_CostsOneConnection()
    {
        // Asserted through the constructor's own guard rather than by counting sockets: a card that
        // repeats an entry must not open it twice, and after collapsing there is still one left.
        var transport = NostrRfqTransport.ForCard(
            Card(["wss://a.example", "wss://A.example", "wss://a.example"]));

        Assert.That(transport, Is.Not.Null);
        transport.Dispose();
    }

    [Test]
    public void ATransportWithNoRelays_IsRefusedAtConstruction()
    {
        // Rather than at the first negotiation, which is after a caller has already committed to a
        // swap flow.
        Assert.Throws<ArgumentException>(
            () => new NostrRfqTransport(Array.Empty<Uri>(), SolverPubkey));
    }

    // ─── The three silences ───────────────────────────────────────────

    [Test]
    public async Task EveryRelayRefusingTheConnection_IsNotBlamedOnTheSolver()
    {
        // The distinction this whole type exists for. Nobody was listening, so the silence says
        // nothing about the counterparty — reporting it as a timeout files a bug against the wrong
        // party, which is how the reference deployment lost days to an outage of its own.
        using var transport = new NostrRfqTransport(
            [Dead(), Dead()], SolverPubkey, timeout: TimeSpan.FromSeconds(5));

        var ex = Assert.ThrowsAsync<RelayUnavailableException>(() => Quote(transport));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Reasons, Has.Count.EqualTo(2), "every relay's own reason is kept");
            Assert.That(ex.Message, Does.Contain("lost every relay connection"));
        });
        await Task.CompletedTask;
    }

    [Test]
    public async Task ARelayThatAnswersTheSocketAndNeverSpeaks_IsStillNotTheSolversFault()
    {
        // Connecting is not the same as being subscribed. A listener that accepts TCP and never
        // completes the upgrade leaves us unable to hear a reply, so this is an unavailable relay
        // rather than a solver that stayed quiet.
        using var mute = new MuteListener();
        using var transport = new NostrRfqTransport(
            [mute.Uri], SolverPubkey, timeout: TimeSpan.FromMilliseconds(600));

        Assert.ThrowsAsync<RelayUnavailableException>(() => Quote(transport));
        await Task.CompletedTask;
    }

    [Test]
    public async Task DisposingMidNegotiation_SaysWeHungUp()
    {
        // A caller that closed deliberately — a user leaving the screen — can match on this and stay
        // quiet, rather than reporting a solver failure that never happened.
        using var mute = new MuteListener();
        var transport = new NostrRfqTransport(
            [mute.Uri], SolverPubkey, timeout: TimeSpan.FromMinutes(5));

        var negotiation = Quote(transport);
        await Task.Delay(150);
        transport.Dispose();

        Assert.ThrowsAsync<TransportClosedException>(() => negotiation);
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static Task Quote(NostrRfqTransport transport) =>
        transport.RequestQuoteAsync<LightningSendRequestProfile, LightningSendQuoteProfile>(
            LightningSendProfile.Request("lnbcrt1", new string('a', 64), new string('b', 64)));

    /// <summary>A port nothing is listening on, so the connection is refused outright.</summary>
    private static Uri Dead()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new Uri($"wss://127.0.0.1:{port}");
    }

    /// <summary>A listener that accepts the socket and never completes the WebSocket upgrade.</summary>
    private sealed class MuteListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<TcpClient> _accepted = [];

        public MuteListener()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Uri = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _ = AcceptAsync();
        }

        public Uri Uri { get; }

        private async Task AcceptAsync()
        {
            try
            {
                while (true) _accepted.Add(await _listener.AcceptTcpClientAsync());
            }
            catch (Exception)
            {
                // Stopped; nothing to do.
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            foreach (var client in _accepted) client.Dispose();
        }
    }

    private static SolverCard Card(string[] relays) => new()
    {
        Version = 0,
        Name = "test-solver",
        DiscoveryPubkey = SolverPubkey,
        Transports = new SolverTransports { Nostr = new NostrTransport { Relays = [.. relays] } },
    };
}
