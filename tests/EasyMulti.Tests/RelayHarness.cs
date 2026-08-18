#nullable enable

using System.Net;
using System.Net.Sockets;
using EasyMultiNet.Relay;
using Xunit;

namespace EasyMultiNet.Tests;

/// <summary>
/// 所有起真中继的用例都归到这一个集合里，串行跑。
/// <para>
/// 原因是下面挑端口的办法有竞争：先绑一个端口问出号码、再放开、之后才由中继去绑。并行的
/// 两个测试类可能拿到同一个号，于是客户端把包发给了别的用例的中继（token 不同 → 被拒），
/// 表现成随机的、跟被测逻辑毫无关系的失败。串行掉最省事，也不用在测试基座里造更聪明的东西。
/// </para>
/// </summary>
[CollectionDefinition(RelayCollection.Name, DisableParallelization = true)]
public sealed class RelayCollection
{
    public const string Name = "relay";
}

/// <summary>Runs a relay on ephemeral ports in a background thread.</summary>
internal sealed class RelayHarness : IDisposable
{
    public const string DefaultToken = "test-token";

    private readonly RelayServer _server;
    private readonly Thread _thread;

    public RelayHarness(string token = DefaultToken)
    {
        WsPort = FreeTcpPort();
        UdpPort = FreeUdpPort();
        var config = new RelayConfig
        {
            Token = token,
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

    /// <summary>中继内部还留着多少东西（连接 / gameId 条目 / 房间 / 房间列表缓存）。</summary>
    public (int Peers, int Games, int Rooms, int ListCache) Snapshot() => _server.Snapshot();

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
