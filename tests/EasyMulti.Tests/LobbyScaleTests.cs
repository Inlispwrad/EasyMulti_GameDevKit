#nullable enable

using System.Diagnostics;
using EasyMultiNet.Protocol;
using Xunit;

namespace EasyMultiNet.Tests;

/// <summary>
/// One relay is expected to hold a four-digit number of connections, so the room list is
/// <b>pull only</b>: it goes out exactly once per LIST_ROOMS and never otherwise.
/// <para>
/// This is not a tuning choice. Pushing it would fan a room-sized payload to every lobby
/// peer on every room event; those payloads pile up in slow peers' send buffers, a peer that
/// overflows gets closed, and a close is itself a room event that fans out again. The process
/// then dies at a moment unrelated to whatever anyone was doing. These tests pin the
/// no-push rule so it cannot be "optimized" back in.
/// </para>
/// </summary>
[Collection(RelayCollection.Name)]
public class LobbyScaleTests
{
    private const string Token = "test-token";
    private const string Game = "test-game";

    [Fact]
    public void RoomList_IsNeverPushed_NotEvenOnRegisterOrRoomEvents()
    {
        using var relay = new RelayHarness();

        var watcher = RelaySession.CreateUdp(Config("Watcher"));
        int lists = 0;
        watcher.RoomListChanged += _ => lists++;
        Connect(watcher, relay.UdpPort);

        // 注册本身不该带来一份列表。
        Pump(1000, () => false, watcher);
        Assert.Equal(0, lists);

        // 一串房间事件也不该。
        var hosts = new List<RelaySession>();
        for (int i = 0; i < 8; i++)
        {
            var host = RelaySession.CreateUdp(Config($"Host{i}"));
            hosts.Add(host);
            Connect(host, relay.UdpPort, All(hosts, watcher));
            host.CreateRoom($"Room{i}");
            Pump(1500, () => host.State == SessionState.InRoom, All(hosts, watcher));
        }

        Pump(1500, () => false, All(hosts, watcher));
        Assert.Equal(0, lists);

        // 问了才给，而且给的是当前真实状态。
        IReadOnlyList<RoomInfo> seen = Array.Empty<RoomInfo>();
        watcher.RoomListChanged += rooms => seen = rooms;
        watcher.RefreshRooms();
        Pump(3000, () => lists == 1, All(hosts, watcher));

        Assert.Equal(1, lists);
        Assert.Equal(8, seen.Count);

        foreach (RelaySession host in hosts) host.Dispose();
        watcher.Dispose();
    }

    [Fact]
    public void EachPull_ReflectsEveryRoomChange()
    {
        // The serialized answer is cached, which is only safe if every mutation drops it.
        using var relay = new RelayHarness();

        var host = RelaySession.CreateUdp(Config("Host"));
        var watcher = RelaySession.CreateUdp(Config("Watcher"));
        Connect(host, relay.UdpPort, host, watcher);
        Connect(watcher, relay.UdpPort, host, watcher);

        IReadOnlyList<RoomInfo> seen = Array.Empty<RoomInfo>();
        watcher.RoomListChanged += rooms => seen = rooms;

        host.CreateRoom("R", maxPlayers: 4);
        Pump(3000, () => host.State == SessionState.InRoom, host, watcher);
        Pull(watcher, () => seen.Count == 1, host, watcher);
        Assert.Equal(0, seen[0].PlayerCount); // host 不是玩家，刚开的房 0 名玩家
        Assert.False(seen[0].InGame);

        var guest = RelaySession.CreateUdp(Config("Guest"));
        Connect(guest, relay.UdpPort, host, watcher, guest);
        guest.JoinRoom(seen[0].Code);
        Pump(3000, () => guest.State == SessionState.InRoom, host, watcher, guest);
        Pull(watcher, () => seen.Count == 1 && seen[0].PlayerCount == 1, host, watcher, guest);
        Assert.Equal(1, seen[0].PlayerCount);

        host.StartGame();
        Pump(1000, () => false, host, watcher, guest);
        Pull(watcher, () => seen.Count == 1 && seen[0].InGame, host, watcher, guest);
        Assert.True(seen[0].InGame);

        host.Dispose();
        guest.Dispose();
        watcher.Dispose();
    }

    [Fact]
    public void AutoHostTransfer_ShowsUpInTheNextPull()
    {
        // The transfer swaps players[0] without touching the player count — exactly the kind
        // of change a naive cache would miss.
        //
        // WebSocket rather than UDP: closing a TCP socket tells the relay immediately, while
        // a vanished UDP peer is only noticed by the idle timeout (60 s).
        using var relay = new RelayHarness();

        var host = RelaySession.CreateWebSocket(Config("Host"));
        var guest = RelaySession.CreateWebSocket(Config("Guest"));
        var watcher = RelaySession.CreateWebSocket(Config("Watcher"));
        Connect(host, relay.WsPort, host, guest, watcher);
        Connect(guest, relay.WsPort, host, guest, watcher);
        Connect(watcher, relay.WsPort, host, guest, watcher);

        IReadOnlyList<RoomInfo> seen = Array.Empty<RoomInfo>();
        watcher.RoomListChanged += rooms => seen = rooms;

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom("R", maxPlayers: 4, autoHostTransfer: true);
        Pump(3000, () => code.Length > 0, host, guest, watcher);

        guest.JoinRoom(code);
        Pump(3000, () => guest.State == SessionState.InRoom, host, guest, watcher);
        Pull(watcher, () => seen.Count == 1 && seen[0].HostId == "Host", host, guest, watcher);
        Assert.Equal("Host", seen[0].HostId);

        host.Dispose(); // 房主掉线 → 顺延给 Guest
        Pump(3000, () => guest.HostId == "Guest", guest, watcher);
        Pull(watcher, () => seen.Count == 1 && seen[0].HostId == "Guest", guest, watcher);
        Assert.Equal("Guest", seen[0].HostId);

        guest.Dispose();
        watcher.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SessionConfig Config(string name) => new(Token, Game, name);

    /// <summary>Ask for the list (repeatedly, until the expectation holds) — nothing arrives unasked.</summary>
    private static void Pull(RelaySession asker, Func<bool> done, params RelaySession[] clients)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            asker.RefreshRooms();
            Pump(400, done, clients);
            if (done()) return;
        }
    }

    private static RelaySession[] All(List<RelaySession> many, params RelaySession[] extra)
    {
        var all = new List<RelaySession>(many);
        all.AddRange(extra);
        return all.ToArray();
    }

    private static void Connect(RelaySession client, int port, params RelaySession[] all)
    {
        client.Connect("127.0.0.1", port);
        RelaySession[] pumped = all.Length > 0 ? all : new[] { client };
        Pump(5000, () => client.State == SessionState.Lobby, pumped);
        Assert.Equal(SessionState.Lobby, client.State);
    }

    private static void Pump(int timeoutMs, Func<bool> done, params RelaySession[] clients)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (RelaySession client in clients) client.Poll();
            if (done()) return;
            Thread.Sleep(5);
        }
    }
}
