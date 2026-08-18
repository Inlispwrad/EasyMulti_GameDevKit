#nullable enable

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyMultiNet.Protocol;
using EasyMultiNet.Relay;
using Xunit;

namespace EasyMultiNet.Tests;

/// <summary>
/// End-to-end tests: spin up a real relay in-process and drive the real client SDK over
/// WebSocket and UDP, including cross-transport interop and UDP fragmentation.
/// </summary>
[Collection(RelayCollection.Name)]
public class RelayIntegrationTests
{
    private const string Token = "test-token";
    private const string Game = "test-game";

    // ── WebSocket ↔ WebSocket ─────────────────────────────────────────────────

    [Fact]
    public void WebSocket_CreateJoin_ExchangeData()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom("Test Room", 4);
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest);
        Assert.NotEqual("", code);

        var guestJoined = new List<string>();
        guest.RoomJoined += c => guestJoined.Add(c);
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest);
        Assert.Equal(code, guestJoined.Single());

        var guestData = new List<(string From, string Data)>();
        guest.GameDataReceived += (from, data) => guestData.Add((from, Encoding.UTF8.GetString(data)));
        host.SendGameData(B("hello-guest"));
        Pump(() => guestData.Count == 1, 5000, host, guest);
        Assert.Equal(("Host", "hello-guest"), guestData.Single());

        var hostData = new List<(string From, string Data)>();
        host.GameDataReceived += (from, data) => hostData.Add((from, Encoding.UTF8.GetString(data)));
        guest.SendGameData(B("hello-host"));
        Pump(() => hostData.Count == 1, 5000, host, guest);
        Assert.Equal(("Guest", "hello-host"), hostData.Single());
        Assert.DoesNotContain(guestData, g => g.Data == "hello-host");
    }

    // ── UDP ↔ UDP ─────────────────────────────────────────────────────────────

    [Fact]
    public void Udp_CreateJoin_ExchangeData()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateUdp(Config("Host"));
        var guest = RelaySession.CreateUdp(Config("Guest"));

        ConnectAndRegister(host, relay.UdpPort, host);
        ConnectAndRegister(guest, relay.UdpPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom();
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest);

        var guestData = new List<(string From, string Data)>();
        guest.GameDataReceived += (from, data) => guestData.Add((from, Encoding.UTF8.GetString(data)));
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest);

        host.SendGameData(B("udp-hello"));
        Pump(() => guestData.Count == 1, 5000, host, guest);
        Assert.Equal(("Host", "udp-hello"), guestData.Single());

        host.SendGameData(B("unreliable-tick"), mode: DeliveryMode.Unreliable);
        Pump(() => guestData.Count == 2, 5000, host, guest);
        Assert.Equal("unreliable-tick", guestData[1].Data);
    }

    // ── WebSocket ↔ UDP interop ───────────────────────────────────────────────

    [Fact]
    public void WebSocket_Udp_Interop()
    {
        using var relay = new RelayHarness();
        var wsHost = RelaySession.CreateWebSocket(Config("Host"));
        var udpGuest = RelaySession.CreateUdp(Config("Guest"));

        ConnectAndRegister(wsHost, relay.WsPort, wsHost);
        ConnectAndRegister(udpGuest, relay.UdpPort, wsHost, udpGuest);

        string code = "";
        wsHost.RoomCreated += c => code = c;
        wsHost.CreateRoom();
        Pump(() => wsHost.State == SessionState.InRoom, 5000, wsHost, udpGuest);

        var guestData = new List<(string From, string Data)>();
        udpGuest.GameDataReceived += (from, data) => guestData.Add((from, Encoding.UTF8.GetString(data)));
        udpGuest.JoinRoom(code);
        Pump(() => udpGuest.State == SessionState.InRoom, 5000, wsHost, udpGuest);

        wsHost.SendGameData(B("ws-to-udp"));
        Pump(() => guestData.Count == 1, 5000, wsHost, udpGuest);
        Assert.Equal(("Host", "ws-to-udp"), guestData.Single());

        var hostData = new List<(string From, string Data)>();
        wsHost.GameDataReceived += (from, data) => hostData.Add((from, Encoding.UTF8.GetString(data)));
        udpGuest.SendGameData(B("udp-to-ws"));
        Pump(() => hostData.Count == 1, 5000, wsHost, udpGuest);
        Assert.Equal(("Guest", "udp-to-ws"), hostData.Single());
    }

    // ── Auth and isolation ────────────────────────────────────────────────────

    [Fact]
    public void BadToken_Rejected()
    {
        // WebSocket：凭证在升级握手里就被驳回（HTTP 401），连接从未建立。
        // 客户端知道自己被拒了，但**拿不到机器可读的理由** —— WebSocket 规范不把失败握手的
        // 响应交给 JS，浏览器如此，ClientWebSocket 在 netstandard2.1 上也只有异常文本。
        // 走到这条路的只有 token/gameId 配错的开发者，原因在中继日志里；玩家会碰到的
        // name_taken 不走这条路（见 NameTaken_IsRefusedOverTheConnection_SoTheReasonArrives）。
        using var relay = new RelayHarness();
        var client = RelaySession.CreateWebSocket(new SessionConfig("wrong-token", Game, "Bad"));
        var failed = new List<string>();
        client.Rejected += failed.Add;
        client.Connect("127.0.0.1", relay.WsPort);
        Pump(() => failed.Count > 0, 5000, client);
        Assert.NotEqual(SessionState.Lobby, client.State);
    }

    /// <summary>UDP 侧的拒绝**带得动理由** —— 帧格式是我们自己的，回一个 Bye 就能捎上原因。</summary>
    [Fact]
    public void BadToken_OverUdp_CarriesTheReason()
    {
        using var relay = new RelayHarness();
        var client = RelaySession.CreateUdp(new SessionConfig("wrong-token", Game, "Bad"));
        var failed = new List<string>();
        client.Rejected += failed.Add;
        client.Connect("127.0.0.1", relay.UdpPort);
        Pump(() => failed.Count > 0, 5000, client);
        Assert.Contains("bad_token", failed.First());
    }

    /// <summary>
    /// name_taken 不是鉴权失败：对方已经用有效 token 证明了身份，所以连接正常建立，
    /// 理由走消息通道回去 —— 浏览器也收得到，UI 能提示「换个名字」。
    /// </summary>
    [Fact]
    public void NameTaken_IsRefusedOverTheConnection_SoTheReasonArrives()
    {
        using var relay = new RelayHarness();
        var first = RelaySession.CreateWebSocket(Config("Dup"));
        ConnectAndRegister(first, relay.WsPort, first);

        var second = RelaySession.CreateWebSocket(Config("Dup"));
        var failed = new List<string>();
        second.Rejected += failed.Add;
        second.Connect("127.0.0.1", relay.WsPort);
        Pump(() => failed.Count > 0, 5000, first, second);
        Assert.Contains("name_taken", failed.First());
    }

    [Fact]
    public void GameId_Isolation()
    {
        using var relay = new RelayHarness();
        var alpha = RelaySession.CreateWebSocket(new SessionConfig(Token, "game-alpha", "Host"));
        var beta = RelaySession.CreateWebSocket(new SessionConfig(Token, "game-beta", "Host"));

        ConnectAndRegister(alpha, relay.WsPort, alpha);
        ConnectAndRegister(beta, relay.WsPort, alpha, beta);

        string code = "";
        alpha.RoomCreated += c => code = c;
        alpha.CreateRoom("Alpha Room", 4);
        Pump(() => alpha.State == SessionState.InRoom, 5000, alpha, beta);

        beta.RefreshRooms();
        // Wait for the ROOM_LIST round-trip, then assert no cross-game leakage.
        Pump(() => beta.Rooms.Count == 0, 5000, alpha, beta);
        Assert.DoesNotContain(beta.Rooms, r => r.Code == code);
    }

    // ── Membership and in-game filtering ──────────────────────────────────────

    [Fact]
    public void NonMember_CannotSendOrReceive()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));
        var outsider = RelaySession.CreateWebSocket(Config("Outsider")); // 留在大厅，不进房

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);
        ConnectAndRegister(outsider, relay.WsPort, host, guest, outsider);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom();
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest, outsider);
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest, outsider);

        // 房间外的人（大厅）发 GAME_DATA，中继应丢弃，成员收不到。
        var memberData = new List<(string From, string Data)>();
        host.GameDataReceived += (from, data) => memberData.Add((from, Encoding.UTF8.GetString(data)));
        guest.GameDataReceived += (from, data) => memberData.Add((from, Encoding.UTF8.GetString(data)));
        outsider.SendGameData(B("intrusion"));
        PollSilence(host, guest, outsider);
        Assert.Empty(memberData);

        // 房间成员定向发给「非成员名」，也应被丢弃。
        var outsiderData = new List<(string From, string Data)>();
        outsider.GameDataReceived += (from, data) => outsiderData.Add((from, Encoding.UTF8.GetString(data)));
        guest.SendGameData(B("to-outsider"), to: "Outsider");
        PollSilence(host, guest, outsider);
        Assert.Empty(outsiderData);
    }

    [Fact]
    public void InGameRoom_FilterableAndLeaverIsolated()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));
        var watcher = RelaySession.CreateWebSocket(Config("Watcher")); // 留在大厅观察列表

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);
        ConnectAndRegister(watcher, relay.WsPort, host, guest, watcher);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom();
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest, watcher);
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest, watcher);

        // 开局前：可加入。
        PumpAsking(watcher, () => watcher.Rooms.Any(r => r.Code == code && !r.InGame), 5000, host, guest, watcher);
        Assert.Contains(watcher.JoinableRooms, r => r.Code == code);

        // 开局后：inGame=true，JoinableRooms 不再包含它。
        host.StartGame();
        Pump(() => host.State == SessionState.InGame, 5000, host, guest, watcher);
        PumpAsking(watcher, () => watcher.Rooms.Any(r => r.Code == code && r.InGame), 5000, host, guest, watcher);
        Assert.DoesNotContain(watcher.JoinableRooms, r => r.Code == code);

        // 开局后离开的人不能再发。
        guest.LeaveRoom();
        Pump(() => guest.State == SessionState.Lobby, 5000, host, guest, watcher);

        var hostData = new List<(string From, string Data)>();
        host.GameDataReceived += (from, data) => hostData.Add((from, Encoding.UTF8.GetString(data)));
        guest.SendGameData(B("after-leave"));
        PollSilence(host, guest, watcher);
        Assert.Empty(hostData);

        // 房主广播，离开的 guest 也收不到。
        var guestData = new List<(string From, string Data)>();
        guest.GameDataReceived += (from, data) => guestData.Add((from, Encoding.UTF8.GetString(data)));
        host.SendGameData(B("to-all"));
        PollSilence(host, guest, watcher);
        Assert.Empty(guestData);
    }

    [Fact]
    public void DisconnectedMember_CanReconnectToInGameRoom()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom();
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest);
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest);
        host.StartGame();
        Pump(() => host.State == SessionState.InGame, 5000, host, guest);

        // 模拟 guest 掉线：关连接，中继保留其座位（名单还在，不限时）。
        var hostSawDisconnect = new List<string>();
        host.PlayerDisconnected += hostSawDisconnect.Add;
        guest.Dispose();
        Pump(() => hostSawDisconnect.Contains("Guest"), 5000, host);
        Assert.Contains("Guest", hostSawDisconnect);

        // 同名重连 + 重新加入（房间已开局）；Host 能收到「重连」事件以便补发。
        var guest2 = RelaySession.CreateWebSocket(Config("Guest"));
        ConnectAndRegister(guest2, relay.WsPort, host, guest2);
        string rejoined = "";
        var hostSawReconnect = new List<string>();
        guest2.RoomJoined += c => rejoined = c;
        host.PlayerReconnected += hostSawReconnect.Add;
        guest2.JoinRoom(code);
        Pump(() => guest2.State == SessionState.InGame, 5000, host, guest2);
        Assert.Equal(code, rejoined);
        Pump(() => hostSawReconnect.Contains("Guest"), 5000, host, guest2); // host 收到重连事件
        Assert.Contains("Guest", hostSawReconnect);

        // 重连后能正常收发。
        var hostData = new List<(string From, string Data)>();
        host.GameDataReceived += (from, data) => hostData.Add((from, Encoding.UTF8.GetString(data)));
        guest2.SendGameData(B("back-online"));
        Pump(() => hostData.Count == 1, 5000, host, guest2);
        Assert.Equal(("Guest", "back-online"), hostData.Single());

        guest2.Dispose();
    }

    [Fact]
    public void HostCanKickMember()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom();
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest);
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest);

        var guestLeft = false;
        guest.LeftRoom += () => guestLeft = true;
        host.Kick("Guest");

        // guest 被踢回大厅，Host 的玩家名单空了（host 自己本来就不在名单里）。
        Pump(() => guest.State == SessionState.Lobby, 5000, host, guest);
        Assert.True(guestLeft);
        Pump(() => host.RoomPlayers.Count == 0, 5000, host, guest);

        guest.Dispose();
    }

    [Fact]
    public void HostLeave_DisbandsRoom()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));
        var watcher = RelaySession.CreateWebSocket(Config("Watcher"));

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);
        ConnectAndRegister(watcher, relay.WsPort, host, guest, watcher);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom();
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest, watcher);
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest, watcher);

        // 房主主动 LEAVE = 解散：玩家被送回大厅，房间从列表消失。
        var guestLeft = false;
        guest.LeftRoom += () => guestLeft = true;
        host.LeaveRoom();
        Pump(() => guestLeft, 5000, host, guest, watcher);
        Assert.Equal(SessionState.Lobby, guest.State);
        PumpAsking(watcher, () => !watcher.Rooms.Any(r => r.Code == code), 5000, host, guest, watcher);

        guest.Dispose();
        watcher.Dispose();
    }

    [Fact]
    public void HostReconnect_PlayersSeeDroppedThenBack()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom();
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest);
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest);

        // 房主断线（没发 LEAVE）：座位保留，玩家收 HOST_DROPPED，房间还在。
        var dropped = false;
        guest.HostDropped += () => dropped = true;
        host.Dispose();
        Pump(() => dropped, 5000, guest);
        Assert.Equal("Host", guest.HostId); // 房主没换

        // 同名重连 + JOIN 同一房码 → 坐回 host 席位，玩家收 HOST_BACK。
        var back = false;
        guest.HostBack += () => back = true;
        var host2 = RelaySession.CreateWebSocket(Config("Host"));
        ConnectAndRegister(host2, relay.WsPort, guest, host2);
        host2.JoinRoom(code);
        Pump(() => back, 5000, guest, host2);
        Assert.True(host2.IsHost);
        Assert.Equal(new[] { "Guest" }, host2.RoomPlayers);

        // 回归的房主能正常收发。
        var guestData = new List<(string From, string Data)>();
        guest.GameDataReceived += (from, data) => guestData.Add((from, Encoding.UTF8.GetString(data)));
        host2.SendGameData(B("welcome-back"));
        Pump(() => guestData.Count == 1, 5000, guest, host2);
        Assert.Equal(("Host", "welcome-back"), guestData.Single());

        host2.Dispose();
        guest.Dispose();
    }

    [Fact]
    public void AutoHostTransfer_On_MigratesToNextPlayer()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom(autoHostTransfer: true);
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest);
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest);

        var hostChanges = new List<string>();
        guest.HostChanged += hostChanges.Add;
        host.Dispose(); // 房主掉线 → 提拔 Guest：从玩家名单里提出来，立为新 host

        Pump(() => hostChanges.Contains("Guest"), 5000, guest);
        Assert.Equal("Guest", guest.HostId);
        Assert.True(guest.IsHost);
        Assert.Empty(guest.RoomPlayers); // 他不再是玩家

        guest.Dispose();
    }

    [Fact]
    public void AutoHostTransfer_Off_HostSeatReserved()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom(); // 默认 autoHostTransfer=false
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest);
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest);

        var hostChanges = new List<string>();
        var hostDropped = false;
        guest.HostChanged += hostChanges.Add;
        guest.HostDropped += () => hostDropped = true;
        host.Dispose();

        Pump(() => hostDropped, 5000, guest);
        Assert.Empty(hostChanges);            // 不转交
        Assert.Equal("Host", guest.HostId); // 房主还是 Host（座位保留）
        Assert.Equal(new[] { "Guest" }, guest.RoomPlayers); // 名单里只有玩家自己

        guest.Dispose();
    }

    [Fact]
    public void RoomDestroyed_WhenNoLiveMembers()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));
        var watcher = RelaySession.CreateWebSocket(Config("Watcher")); // 大厅观察者

        ConnectAndRegister(host, relay.WsPort, host);
        ConnectAndRegister(guest, relay.WsPort, host, guest);
        ConnectAndRegister(watcher, relay.WsPort, host, guest, watcher);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom(autoHostTransfer: true);
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest, watcher);
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest, watcher);
        PumpAsking(watcher, () => watcher.Rooms.Any(r => r.Code == code), 5000, host, guest, watcher);

        // 所有人掉线 → 没有在线成员 → 房间销毁。
        host.Dispose();
        guest.Dispose();
        PumpAsking(watcher, () => !watcher.Rooms.Any(r => r.Code == code), 5000, watcher);

        watcher.Dispose();
    }

    // ── UDP fragmentation ─────────────────────────────────────────────────────

    [Fact]
    public void Udp_LargeReliableMessage_FragmentedAndReassembled()
    {
        using var relay = new RelayHarness();
        var host = RelaySession.CreateUdp(Config("Host"));
        var guest = RelaySession.CreateUdp(Config("Guest"));

        ConnectAndRegister(host, relay.UdpPort, host);
        ConnectAndRegister(guest, relay.UdpPort, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom();
        Pump(() => host.State == SessionState.InRoom, 5000, host, guest);

        var guestData = new List<(string From, string Data)>();
        guest.GameDataReceived += (from, data) => guestData.Add((from, Encoding.UTF8.GetString(data)));
        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest);

        // Far larger than the ~1180-byte UDP payload budget → must be fragmented.
        string big = new string('Z', 5000);
        host.SendGameData(B(big));
        Pump(() => guestData.Count == 1, 10000, host, guest);
        Assert.Equal(("Host", big), guestData.Single());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SessionConfig Config(string name) => new(Token, Game, name);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static void ConnectAndRegister(RelaySession client, int port, params RelaySession[] all)
    {
        client.Connect("127.0.0.1", port);
        Pump(() => client.State == SessionState.Lobby, 5000, all);
        Assert.Equal(SessionState.Lobby, client.State);
    }

    /// <summary>
    /// 边等边问。房间列表是「问才有」的 —— 中继绝不主动推，所以等待它变化的用例
    /// 必须自己周期性 RefreshRooms。
    /// </summary>
    private static void PumpAsking(RelaySession asker, Func<bool> done, int timeoutMs, params RelaySession[] clients)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            asker.RefreshRooms();
            var round = Stopwatch.StartNew();
            while (!done() && round.ElapsedMilliseconds < 200)
            {
                foreach (RelaySession client in clients)
                {
                    client.Poll();
                }

                Thread.Sleep(5);
            }
        }

        Assert.True(done(), $"条件在 {timeoutMs}ms 内未满足（已周期性 RefreshRooms）");
    }

    private static void Pump(Func<bool> done, int timeoutMs, params RelaySession[] clients)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (RelaySession client in clients)
            {
                client.Poll();
            }

            Thread.Sleep(5);
        }

        Assert.True(done(), $"条件在 {timeoutMs}ms 内未满足");
    }

    /// <summary>轮询一段时间并保持安静——用于「断言什么都没发生」的反向用例。</summary>
    private static void PollSilence(params RelaySession[] clients)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500)
        {
            foreach (RelaySession client in clients)
            {
                client.Poll();
            }

            Thread.Sleep(5);
        }
    }

}
