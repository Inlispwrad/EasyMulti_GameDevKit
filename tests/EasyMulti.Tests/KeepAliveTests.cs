#nullable enable

using System.Diagnostics;
using EasyMultiNet.Protocol;
using Xunit;

namespace EasyMultiNet.Tests;

/// <summary>
/// 保活：安静的连接不能被自己的 idle 超时杀掉，但真死的连接还是得被清掉。
/// <para>
/// 两条不能互相牺牲 —— 前者没有的话，回合制游戏思考一分钟就全员掉线；
/// 后者破了的话，僵尸连接会一直占着席位和内存。
/// </para>
/// <para>
/// 这里把两个 <see cref="UdpPeer"/> 直接对接（一端的发送就是另一端的接收），
/// 不走 socket，所以超时值可以调到几百毫秒，测试跑得快也不看运气。
/// </para>
/// </summary>
public class KeepAliveTests
{
    private const long IdleMs = 400;
    private const long PingMs = 100;

    /// <summary>安静地待着超过 idle 超时 —— 两端都该活着，靠的是客户端定时发的 PING。</summary>
    [Fact]
    public void SilentConnection_SurvivesPastTheIdleTimeout()
    {
        (UdpPeer client, UdpPeer relay) = Linked(clientKeepAlive: PingMs);

        Run(IdleMs * 2, client, relay);

        Assert.False(client.IsClosed, "客户端被自己的 idle 超时杀了");
        Assert.False(relay.IsClosed, "中继把一条还活着的连接判成了超时");
    }

    /// <summary>
    /// 客户端进程真的没了（不再 Tick、也收不到东西）—— 中继必须照常在 idle 超时后清掉它。
    /// 中继侧不主动发保活（KeepAliveMs=0），所以它的判断完全依赖对方还在不在发东西。
    /// </summary>
    [Fact]
    public void DeadClient_IsStillDroppedByTheRelay()
    {
        (UdpPeer client, UdpPeer relay) = Linked(clientKeepAlive: PingMs);

        Run(IdleMs / 4, client, relay); // 先正常活一会儿
        Assert.False(relay.IsClosed);

        Run(IdleMs * 2, relay); // 客户端不再 Tick，等于进程没了

        Assert.True(relay.IsClosed, "客户端已经没了，中继却还留着这条连接");
    }

    /// <summary>
    /// 保活不能把僵尸连接救活：一端一直发 PING、但对面根本不回，它仍然要超时。
    /// 因为 <c>_lastActivityMs</c> 只被**收到的**包刷新 —— 自己发东西不算自己还活着。
    /// </summary>
    [Fact]
    public void PingingIntoTheVoid_StillTimesOut()
    {
        var sent = 0;
        var lonely = new UdpPeer(
            "lonely",
            send: (_, _) => sent++, // 发得出去，但没有任何人回
            deliver: (_, _, _) => { },
            close: _ => { },
            new UdpPeerOptions { IdleTimeoutMs = IdleMs, KeepAliveMs = PingMs });

        Run(IdleMs * 2, lonely);

        Assert.True(sent > 0, "保活压根没发出去");
        Assert.True(lonely.IsClosed, "对面没有任何回应，这条连接却没有超时");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把两个 peer 对接起来：一端发出去的字节直接喂给另一端。
    /// 中继那侧 <c>KeepAliveMs</c> 保持 0 —— 它只回应，不主动发（NAT 映射是客户端的事）。
    /// </summary>
    private static (UdpPeer Client, UdpPeer Relay) Linked(long clientKeepAlive)
    {
        UdpPeer? relay = null;

        var client = new UdpPeer(
            "client",
            send: (data, len) => relay!.HandleDatagram(Copy(data, len), len),
            deliver: (_, _, _) => { },
            close: _ => { },
            new UdpPeerOptions { IdleTimeoutMs = IdleMs, KeepAliveMs = clientKeepAlive });

        relay = new UdpPeer(
            "relay",
            send: (data, len) => client.HandleDatagram(Copy(data, len), len),
            deliver: (_, _, _) => { },
            close: _ => { },
            new UdpPeerOptions { IdleTimeoutMs = IdleMs }); // KeepAliveMs = 0：只回应

        return (client, relay);
    }

    /// <summary>发送缓冲区是复用的，喂给对端之前必须拷一份。</summary>
    private static byte[] Copy(byte[] data, int length)
    {
        var copy = new byte[length];
        System.Array.Copy(data, copy, length);
        return copy;
    }

    private static void Run(long millis, params UdpPeer[] peers)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < millis)
        {
            foreach (UdpPeer p in peers) p.Tick();
            Thread.Sleep(5);
        }
    }
}
