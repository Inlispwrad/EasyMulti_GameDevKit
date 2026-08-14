#nullable enable

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EasyMulti.Client;
using EasyMulti.Protocol;
using EasyMulti.Relay;
using Xunit;

namespace EasyMulti.Tests;

/// <summary>
/// End-to-end tests: spin up a real relay in-process and drive the real client SDK over
/// WebSocket and UDP, including cross-transport interop and UDP fragmentation.
/// </summary>
public class RelayIntegrationTests
{
    private const string Token = "test-token";
    private const string Game = "test-game";

    // ── WebSocket ↔ WebSocket ─────────────────────────────────────────────────

    [Fact]
    public void WebSocket_CreateJoin_ExchangeData()
    {
        using var relay = new RelayHarness();
        var host = EasyMultiClient.CreateWebSocket(Config("Host"));
        var guest = EasyMultiClient.CreateWebSocket(Config("Guest"));

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom("Test Room", 4);
        Pump(() => host.State == EasyMultiState.InRoom, 5000, host, guest);
        Assert.NotEqual("", code);

        var guestJoined = new List<string>();
        guest.RoomJoined += c => guestJoined.Add(c);
        guest.JoinRoom(code);
        Pump(() => guest.State == EasyMultiState.InRoom, 5000, host, guest);
        Assert.Equal(code, guestJoined.Single());

        var guestData = new List<(string From, string Data)>();
        guest.GameDataReceived += (from, data) => guestData.Add((from, data));
        host.SendGameData("hello-guest");
        Pump(() => guestData.Count == 1, 5000, host, guest);
        Assert.Equal(("Host", "hello-guest"), guestData.Single());

        var hostData = new List<(string From, string Data)>();
        host.GameDataReceived += (from, data) => hostData.Add((from, data));
        guest.SendGameData("hello-host");
        Pump(() => hostData.Count == 1, 5000, host, guest);
        Assert.Equal(("Guest", "hello-host"), hostData.Single());
        Assert.DoesNotContain(guestData, g => g.Data == "hello-host");
    }

    // ── UDP ↔ UDP ─────────────────────────────────────────────────────────────

    [Fact]
    public void Udp_CreateJoin_ExchangeData()
    {
        using var relay = new RelayHarness();
        var host = EasyMultiClient.CreateUdp(Config("Host"));
        var guest = EasyMultiClient.CreateUdp(Config("Guest"));

        ConnectAndRegister(host, relay.UdpPort, host);
        ConnectAndRegister(guest, relay.UdpPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom();
        Pump(() => host.State == EasyMultiState.InRoom, 5000, host, guest);

        var guestData = new List<(string From, string Data)>();
        guest.GameDataReceived += (from, data) => guestData.Add((from, data));
        guest.JoinRoom(code);
        Pump(() => guest.State == EasyMultiState.InRoom, 5000, host, guest);

        host.SendGameData("udp-hello");
        Pump(() => guestData.Count == 1, 5000, host, guest);
        Assert.Equal(("Host", "udp-hello"), guestData.Single());

        host.SendGameData("unreliable-tick", mode: DeliveryMode.Unreliable);
        Pump(() => guestData.Count == 2, 5000, host, guest);
        Assert.Equal("unreliable-tick", guestData[1].Data);
    }

    // ── WebSocket ↔ UDP interop ───────────────────────────────────────────────

    [Fact]
    public void WebSocket_Udp_Interop()
    {
        using var relay = new RelayHarness();
        var wsHost = EasyMultiClient.CreateWebSocket(Config("Host"));
        var udpGuest = EasyMultiClient.CreateUdp(Config("Guest"));

        ConnectAndRegister(wsHost, relay.WsPort, wsHost);
        ConnectAndRegister(udpGuest, relay.UdpPort, wsHost, udpGuest);

        string code = "";
        wsHost.RoomCreated += c => code = c;
        wsHost.CreateRoom();
        Pump(() => wsHost.State == EasyMultiState.InRoom, 5000, wsHost, udpGuest);

        var guestData = new List<(string From, string Data)>();
        udpGuest.GameDataReceived += (from, data) => guestData.Add((from, data));
        udpGuest.JoinRoom(code);
        Pump(() => udpGuest.State == EasyMultiState.InRoom, 5000, wsHost, udpGuest);

        wsHost.SendGameData("ws-to-udp");
        Pump(() => guestData.Count == 1, 5000, wsHost, udpGuest);
        Assert.Equal(("Host", "ws-to-udp"), guestData.Single());

        var hostData = new List<(string From, string Data)>();
        wsHost.GameDataReceived += (from, data) => hostData.Add((from, data));
        udpGuest.SendGameData("udp-to-ws");
        Pump(() => hostData.Count == 1, 5000, wsHost, udpGuest);
        Assert.Equal(("Guest", "udp-to-ws"), hostData.Single());
    }

    // ── Auth and isolation ────────────────────────────────────────────────────

    [Fact]
    public void BadToken_Rejected()
    {
        using var relay = new RelayHarness();
        var client = EasyMultiClient.CreateWebSocket(new EasyMultiConfig("wrong-token", Game, "Bad"));
        var failed = new List<string>();
        client.Failed += failed.Add;
        client.Connect("127.0.0.1", relay.WsPort);
        Pump(() => failed.Count > 0, 5000, client);
        Assert.Contains("bad_token", failed.First());
    }

    [Fact]
    public void GameId_Isolation()
    {
        using var relay = new RelayHarness();
        var alpha = EasyMultiClient.CreateWebSocket(new EasyMultiConfig(Token, "game-alpha", "Host"));
        var beta = EasyMultiClient.CreateWebSocket(new EasyMultiConfig(Token, "game-beta", "Host"));

        ConnectAndRegister(alpha, relay.WsPort, alpha);
        ConnectAndRegister(beta, relay.WsPort, alpha, beta);

        string code = "";
        alpha.RoomCreated += c => code = c;
        alpha.CreateRoom("Alpha Room", 4);
        Pump(() => alpha.State == EasyMultiState.InRoom, 5000, alpha, beta);

        beta.RefreshRooms();
        // Wait for the ROOM_LIST round-trip, then assert no cross-game leakage.
        Pump(() => beta.Rooms.Count == 0, 5000, alpha, beta);
        Assert.DoesNotContain(beta.Rooms, r => r.Code == code);
    }

    // ── UDP fragmentation ─────────────────────────────────────────────────────

    [Fact]
    public void Udp_LargeReliableMessage_FragmentedAndReassembled()
    {
        using var relay = new RelayHarness();
        var host = EasyMultiClient.CreateUdp(Config("Host"));
        var guest = EasyMultiClient.CreateUdp(Config("Guest"));

        ConnectAndRegister(host, relay.UdpPort, host);
        ConnectAndRegister(guest, relay.UdpPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom();
        Pump(() => host.State == EasyMultiState.InRoom, 5000, host, guest);

        var guestData = new List<(string From, string Data)>();
        guest.GameDataReceived += (from, data) => guestData.Add((from, data));
        guest.JoinRoom(code);
        Pump(() => guest.State == EasyMultiState.InRoom, 5000, host, guest);

        // Far larger than the ~1180-byte UDP payload budget → must be fragmented.
        string big = new string('Z', 5000);
        host.SendGameData(big);
        Pump(() => guestData.Count == 1, 10000, host, guest);
        Assert.Equal(("Host", big), guestData.Single());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EasyMultiConfig Config(string name) => new(Token, Game, name);

    private static void ConnectAndRegister(EasyMultiClient client, int port, params EasyMultiClient[] all)
    {
        client.Connect("127.0.0.1", port);
        Pump(() => client.State == EasyMultiState.Lobby, 5000, all);
        Assert.Equal(EasyMultiState.Lobby, client.State);
    }

    private static void Pump(Func<bool> done, int timeoutMs, params EasyMultiClient[] clients)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (EasyMultiClient client in clients)
            {
                client.Poll();
            }

            Thread.Sleep(5);
        }

        Assert.True(done(), $"条件在 {timeoutMs}ms 内未满足");
    }

    /// <summary>Runs a relay on ephemeral ports in a background thread.</summary>
    private sealed class RelayHarness : IDisposable
    {
        private readonly RelayServer _server;
        private readonly Thread _thread;

        public RelayHarness()
        {
            WsPort = FreeTcpPort();
            UdpPort = FreeUdpPort();
            var config = new RelayConfig
            {
                Token = Token,
                WebSocketEnabled = true,
                WebSocketPort = WsPort,
                UdpEnabled = true,
                UdpPort = UdpPort,
                MaxConnections = 100,
                IdleTimeoutMs = 60_000,
            };
            _server = new RelayServer(config);
            _thread = new Thread(() => _server.Run()) { IsBackground = true };
            _thread.Start();
            Thread.Sleep(250); // let listeners bind
        }

        public int WsPort { get; }
        public int UdpPort { get; }

        public void Dispose()
        {
            _server.Stop();
            _thread.Join(1000);
        }

        private static int FreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static int FreeUdpPort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
    }
}
