#nullable enable

using System.Diagnostics;
using System.Text;
using EasyMultiNet.Protocol;
using Xunit;

namespace EasyMultiNet.Tests;

/// <summary>
/// 「跑久了会不会涨、全断开会不会留东西」的防线 —— 上公网前的必要条件。
/// <para>
/// 覆盖两类容器：<see cref="UdpPeer"/> 的乱序重组表（鉴权之前就存在，谁都能往里塞），
/// 和中继的 gameId 表 + 房间列表缓存（gameId 由客户端自填，基数无限）。
/// </para>
/// </summary>
[Collection(RelayCollection.Name)]
public class RelayResidueTests
{
    private const string Token = RelayHarness.DefaultToken;

    // ── UdpPeer：乱序重组表 ───────────────────────────────────────────────────

    /// <summary>
    /// 窗口之外的 seq 直接丢。没有这条，一个持续发高 seq、就是不补 _recvNext 那一格的源
    /// 就能把重组表撑爆 —— 而 UdpTransport 收到第一个包就建 peer，发生在任何鉴权之前。
    /// </summary>
    [Fact]
    public void OutOfWindow_Seq_IsDropped_NotBuffered()
    {
        UdpPeer peer = NewPeer(out _, window: 8);

        // _recvNext 从 1 起 → 窗口是 [1, 8]，seq 9 已经在外面。
        for (uint seq = 9; seq < 3000; seq++)
        {
            Feed(peer, seq, "x");
        }

        Assert.Equal(0, peer.IncomingCount);
    }

    /// <summary>
    /// 已经交付过的 seq 重传回来，不能再进表 —— 它只会被 DeliverInOrder 从 _recvNext 往上取，
    /// 进去就再也没人取走。重传与 ack 擦肩而过是丢包时的常态，不需要有人攻击。
    /// </summary>
    [Fact]
    public void Retransmit_OfDeliveredSeq_DoesNotReenterTheTable()
    {
        UdpPeer peer = NewPeer(out List<string> delivered, window: 8);

        Feed(peer, 1, "a");
        Feed(peer, 2, "b");
        Assert.Equal(new[] { "a", "b" }, delivered);
        Assert.Equal(0, peer.IncomingCount);

        for (int i = 0; i < 200; i++)
        {
            Feed(peer, 1, "a");
            Feed(peer, 2, "b");
        }

        Assert.Equal(0, peer.IncomingCount);
        Assert.Equal(2, delivered.Count); // 也不能重复交付
    }

    /// <summary>窗口内的乱序照常缓冲、补齐后按序交付 —— 别把真正的 ARQ 一起挡掉了。</summary>
    [Fact]
    public void InWindow_OutOfOrder_StillBuffersAndDrains()
    {
        UdpPeer peer = NewPeer(out List<string> delivered, window: 8);

        Feed(peer, 3, "c");
        Feed(peer, 2, "b");
        Assert.Equal(2, peer.IncomingCount);
        Assert.Empty(delivered);

        Feed(peer, 1, "a"); // 补上缺口
        Assert.Equal(new[] { "a", "b", "c" }, delivered);
        Assert.Equal(0, peer.IncomingCount);
    }

    // ── 中继：gameId 表与房间列表缓存 ─────────────────────────────────────────

    /// <summary>
    /// 拿不存在的房码去 JOIN、以及查一个没有房间的大厅，都会给客户端自填的 gameId 留下条目。
    /// 这些条目要在「有房间被擦除」时被一起收掉，否则每个用过的 gameId 永久占位。
    /// </summary>
    [Fact]
    public void BogusGameIds_AreSweptWhenARoomIsErased()
    {
        using var relay = new RelayHarness();

        const int scanners = 5;
        for (int i = 0; i < scanners; i++)
        {
            var scanner = RelaySession.CreateUdp(new SessionConfig(Token, $"scan-{i}", $"S{i}"));
            scanner.Connect("127.0.0.1", relay.UdpPort);
            Pump(() => scanner.State == SessionState.Lobby, 5000, scanner);

            scanner.JoinRoom("ZZZZZZ"); // 不存在的房码：只为把中继推到 Rooms(gameId) 那一步
            scanner.RefreshRooms();     // 空列表也会进缓存
            PumpFor(300, scanner);
            scanner.Dispose();
        }

        // 先证明这些条目确实留下来了，否则下面的断言等于没测。
        WaitFor(() => relay.Snapshot().Games >= scanners);
        Assert.True(relay.Snapshot().ListCache >= scanners);

        // 正常开一间房，然后房主掉线 → 房间没有在线成员 → 销毁 → 触发清扫。
        var host = RelaySession.CreateUdp(new SessionConfig(Token, "real-game", "Host"));
        host.Connect("127.0.0.1", relay.UdpPort);
        Pump(() => host.State == SessionState.Lobby, 5000, host);
        host.CreateRoom("R", 4);
        Pump(() => host.State == SessionState.InRoom, 5000, host);
        host.Dispose();

        WaitFor(() => relay.Snapshot() == (0, 0, 0, 0));
        Assert.Equal((0, 0, 0, 0), relay.Snapshot());
    }

    /// <summary>一间房从建到全员离开，中继要回到空表 —— 连接、房间、gameId 条目、列表缓存都不留。</summary>
    [Fact]
    public void FullRoomLifecycle_LeavesNothingBehind()
    {
        using var relay = new RelayHarness();

        var host = RelaySession.CreateUdp(new SessionConfig(Token, "g", "Host"));
        var guest = RelaySession.CreateUdp(new SessionConfig(Token, "g", "Guest"));

        host.Connect("127.0.0.1", relay.UdpPort);
        guest.Connect("127.0.0.1", relay.UdpPort);
        Pump(() => host.State == SessionState.Lobby && guest.State == SessionState.Lobby, 5000, host, guest);

        string code = "";
        host.RoomCreated += c => code = c;
        host.CreateRoom("R", 4);
        Pump(() => code.Length > 0, 5000, host, guest);

        guest.JoinRoom(code);
        Pump(() => guest.State == SessionState.InRoom, 5000, host, guest);
        guest.RefreshRooms();
        PumpFor(200, host, guest);
        Assert.Equal(1, relay.Snapshot().Rooms);

        guest.Dispose();
        host.Dispose();

        WaitFor(() => relay.Snapshot() == (0, 0, 0, 0));
        Assert.Equal((0, 0, 0, 0), relay.Snapshot());
    }

    // ── 连接的门口：没验过就不分配 ───────────────────────────────────────────

    /// <summary>
    /// 这次改动真正要证明的那条：**凭证不合格的连接从来不存在**。
    /// 反复用错 token 猛敲两个传输，中继的连接表必须一直是 0 —— 没有「连上了但还没注册」
    /// 的槽位可占，所以匿名来源没法把 MaxConnections 占满、把真玩家挡在外面。
    /// </summary>
    [Fact]
    public void BadCredentials_AllocateNothing_OnEitherTransport()
    {
        using var relay = new RelayHarness();

        for (int i = 0; i < 10; i++)
        {
            var ws = RelaySession.CreateWebSocket(new SessionConfig("wrong-token", $"g{i}", $"P{i}"));
            var udp = RelaySession.CreateUdp(new SessionConfig("wrong-token", $"g{i}", $"Q{i}"));
            ws.Connect("127.0.0.1", relay.WsPort);
            udp.Connect("127.0.0.1", relay.UdpPort);
            PumpFor(120, ws, udp);
            ws.Dispose();
            udp.Dispose();

            Assert.Equal((0, 0, 0, 0), relay.Snapshot());
        }

        // 门关着不代表关死了：合法凭证仍然进得来。
        var good = RelaySession.CreateUdp(new SessionConfig(Token, "g", "Good"));
        good.Connect("127.0.0.1", relay.UdpPort);
        Pump(() => good.State == SessionState.Lobby, 5000, good);
        Assert.Equal(1, relay.Snapshot().Peers);
        good.Dispose();
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────────

    private static UdpPeer NewPeer(out List<string> delivered, int window)
    {
        var received = new List<string>();
        delivered = received;
        return new UdpPeer(
            "test",
            send: (_, _) => { },
            deliver: (payload, _, _) => received.Add(Encoding.UTF8.GetString(payload)),
            close: _ => { },
            new UdpPeerOptions { MaxPendingMessages = window });
    }

    /// <summary>把一条「可靠、不分片」的帧喂给 peer，跳过真实 socket。</summary>
    private static void Feed(UdpPeer peer, uint seq, string payload)
    {
        var buffer = new byte[UdpFrame.DefaultMtu];
        int written = UdpFrame.Write(
            buffer,
            new FrameHeader(FrameFlags.Reliable, seq, 0, 0, 0, 1),
            Encoding.UTF8.GetBytes(payload));
        peer.HandleDatagram(buffer, written);
    }

    private static void Pump(Func<bool> done, int timeoutMs, params RelaySession[] clients)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (RelaySession client in clients) client.Poll();
            Thread.Sleep(5);
        }

        Assert.True(done(), $"条件在 {timeoutMs}ms 内未满足");
    }

    /// <summary>单纯泵一段时间，不带断言（用来让消息真的走出去）。</summary>
    private static void PumpFor(int millis, params RelaySession[] clients)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < millis)
        {
            foreach (RelaySession client in clients) client.Poll();
            Thread.Sleep(5);
        }
    }

    /// <summary>等中继那条线程自己把事件处理完。</summary>
    private static void WaitFor(Func<bool> done, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(10);
    }
}
