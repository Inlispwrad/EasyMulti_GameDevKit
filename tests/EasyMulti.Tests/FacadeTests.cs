#nullable enable

using System.Diagnostics;
using MemoryPack;
using Xunit;

namespace EasyMultiNet.Tests;

// 两端共用的消息类型：T 就是消息通道。
[MemoryPackable]
public partial record MoveMsg(int X, int Y);

[MemoryPackable]
public partial record ChatMsg(string Text);

/// <summary>默认壳的推荐编解码器 —— 与 docs/USAGE.md 里给开发者的拷贝件一字不差。</summary>
public sealed class MemoryPackCodec : IPayloadCodec
{
    public byte[] Encode<T>(T value) => MemoryPackSerializer.Serialize(value);

    public T Decode<T>(ReadOnlySpan<byte> body) => MemoryPackSerializer.Deserialize<T>(body)!;
}

/// <summary>裸字节直通编解码器：验证「管道零膨胀」时把 codec 的影响排除掉。</summary>
internal sealed class RawCodec : IPayloadCodec
{
    public byte[] Encode<T>(T value) => (byte[])(object)value!;

    public T Decode<T>(ReadOnlySpan<byte> body) => (T)(object)body.ToArray();
}

/// <summary>
/// 全局门面（<see cref="EasyMulti"/> + <see cref="EasyMultiClient"/> / <see cref="EasyMultiHost"/>）的行为测试：
/// Init 只写配置、Connect/Open 工厂即连即用、连前排队、类型通道（Send&lt;T&gt;/Receive&lt;T&gt; 按 T 路由、
/// 未注册类型静默丢弃、未挂 codec fail-fast）、玩家口径的名单与大厅计数、Kick、Lock、
/// StandAlone 拒同名玩家、host/玩家掉线重连频道、Rejected 与 Disconnected 分频道、Close 解散、
/// 管道零膨胀直通。中继本身的行为由 RelayIntegrationTests 用低层 <see cref="RelaySession"/> 覆盖。
/// <para>
/// EasyMulti 的配置/编解码器/连接注册表是进程级静态的，所以这些用例留在同一个测试类里
/// （xUnit 类内串行），每个用例结束时 <see cref="Dispose"/> 清干净。
/// </para>
/// </summary>
[Collection(RelayCollection.Name)]
public class FacadeTests : IDisposable
{
    private const string Token = "test-token";
    private const string Game = "facade-game";

    private static readonly MemoryPackCodec Mp = new();

    public void Dispose()
    {
        EasyMulti.Shutdown();
        EasyMulti.Init(new()); // 抹掉进程级静态配置（含 Codec），别泄给下一个用例
    }

    [Fact]
    public void HostRoutedChat_EndToEnd_WithQueuedCommands()
    {
        using var relay = new RelayHarness();
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.UdpPort, Codec = Mp });

        // Open = 建连 + 开房一步到位；开房指令在注册完成前自动排队。
        string code = "";
        EasyMultiHost room = EasyMulti.Host.Open("Alice", "聊天室", players: 4);
        room.Opened += c => code = c;
        Pump(() => code.Length > 0);

        // 「自己开房自己玩」：同名玩家就是房主本人（host 连接叫 Alice#host，不打架）。
        var joined = new List<string>();
        EasyMultiClient me = EasyMulti.Client.Connect("Alice");
        me.Joined += joined.Add;
        me.Join(code);
        Pump(() => joined.Count == 1);
        Assert.Equal(code, me.RoomCode);
        Assert.True(me.Connected);

        // 玩家 → 房主：直接发对象，T 就是通道（这里 T=string，MemoryPack 原生支持）。
        var hostGot = new List<(string From, string Text)>();
        room.Receive<string>((from, text) => hostGot.Add((from, text)));
        me.Send("hello");
        Pump(() => hostGot.Count == 1);
        Assert.Equal(("Alice", "hello"), hostGot.Single());

        // 房主 → 所有玩家 / 单个玩家。
        var meGot = new List<string>();
        me.Receive<string>(meGot.Add);
        room.Broadcast("seq-1");
        Pump(() => meGot.Count == 1);
        room.Send("Alice", "private");
        Pump(() => meGot.Count == 2);
        Assert.Equal(new[] { "seq-1", "private" }, meGot);
    }

    [Fact]
    public void TypedChannel_RoutesByType_AndDropsUnsubscribed()
    {
        using var relay = new RelayHarness();
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.UdpPort });

        string code = "";
        EasyMultiHost room = EasyMulti.Host.Open("Alice", "R", players: 4);
        room.Opened += c => code = c;

        // 没挂 codec 就 Send<T> → fail-fast，别让人静默发不出去。
        Assert.Throws<InvalidOperationException>(() => room.Broadcast(new MoveMsg(1, 2)));
        // 补挂 codec ＝ 重新 Init（整份覆盖）；已经建好的连接不受影响。
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.UdpPort, Codec = Mp });
        Pump(() => code.Length > 0);

        var joined = false;
        EasyMultiClient me = EasyMulti.Client.Connect("Alice");
        me.Joined += _ => joined = true;
        me.Join(code);
        Pump(() => joined);

        // host 只订了 MoveMsg：Move 到手、Chat 静默丢弃（Receive 的类型不同就不会被调）。
        var moves = new List<(string From, MoveMsg Move)>();
        room.Receive<MoveMsg>((from, m) => moves.Add((from, m)));
        me.Send(new ChatMsg("你好"));      // host 没订 ChatMsg → 无人接，静默
        me.Send(new MoveMsg(3, -7));
        Pump(() => moves.Count == 1);
        Assert.Equal(("Alice", new MoveMsg(3, -7)), moves.Single());

        // 玩家侧两条通道并存，各走各的。
        var chats = new List<ChatMsg>();
        var snaps = new List<MoveMsg>();
        me.Receive<ChatMsg>(chats.Add);
        me.Receive<MoveMsg>(snaps.Add);
        room.Broadcast(new ChatMsg("广播"));
        room.Send("Alice", new MoveMsg(9, 9));
        Pump(() => chats.Count == 1 && snaps.Count == 1);
        Assert.Equal(new ChatMsg("广播"), chats.Single());
        Assert.Equal(new MoveMsg(9, 9), snaps.Single());
    }

    [Fact]
    public void RawBytes_PassThrough_Unmodified()
    {
        using var relay = new RelayHarness();
        // RawCodec：排除 codec 影响，验证管道本身零膨胀
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.UdpPort, Codec = new RawCodec() });

        string code = "";
        EasyMultiHost room = EasyMulti.Host.Open("Alice", "R", players: 4);
        room.Opened += c => code = c;
        Pump(() => code.Length > 0);

        var joined = false;
        EasyMultiClient me = EasyMulti.Client.Connect("Alice");
        me.Joined += _ => joined = true;
        me.Join(code);
        Pump(() => joined);

        // 覆盖全部 256 种字节值 + 一个必须 UDP 分片的大负载：逐字节一致 = 零膨胀零转译。
        var small = new byte[300];
        for (int i = 0; i < small.Length; i++) small[i] = (byte)i;
        var big = new byte[6000];
        new Random(42).NextBytes(big);

        var hostGot = new List<byte[]>();
        room.Receive<byte[]>((_, data) => hostGot.Add(data));
        me.Send(small);
        Pump(() => hostGot.Count == 1);
        Assert.Equal(small, hostGot[0]);

        var meGot = new List<byte[]>();
        me.Receive<byte[]>(meGot.Add);
        room.Broadcast(big); // 超过单帧 payload 预算 → UDP 自动分片重组
        Pump(() => meGot.Count == 1);
        Assert.Equal(big, meGot[0]);
    }

    [Fact]
    public void PlayersListAndLobby_CountPlayersOnly()
    {
        using var relay = new RelayHarness();
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.UdpPort });

        string code = "";
        EasyMultiHost room = EasyMulti.Host.Open("Alice", "R", players: 3);
        room.Opened += c => code = c;
        Pump(() => code.Length > 0);

        // 大厅口径：刚开的房 0 名玩家，容量就是要的 3 —— 没有任何 ±1。
        EasyMultiClient me = EasyMulti.Client.Connect("Alice");
        IReadOnlyList<Room>? rooms = null;
        me.RoomsChanged += r => rooms = r;
        PumpAsking(me, () => rooms is { Count: 1 });
        Assert.Equal(0, rooms![0].Players);
        Assert.Equal(3, rooms[0].Capacity);

        // 名单口径：只有玩家，host 从来不在里面。
        IReadOnlyList<string>? seats = null;
        room.PlayersChanged += s => seats = s;
        me.Join(code);
        Pump(() => seats is { Count: 1 });
        Assert.Equal(new[] { "Alice" }, seats);

        var bob = RelaySession.CreateUdp(new SessionConfig(Token, Game, "Bob"));
        bob.Connect("127.0.0.1", relay.UdpPort);
        bob.JoinRoom(code);
        Pump(() => seats is { Count: 2 }, bob);
        Assert.Equal(new[] { "Alice", "Bob" }, seats);

        PumpAsking(me, () => rooms is { Count: 1 } && rooms[0].Players == 2, bob);
        bob.Dispose();
    }

    [Fact]
    public void Lock_KeepsNewcomersOut()
    {
        using var relay = new RelayHarness();
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.UdpPort });

        string code = "";
        EasyMultiHost room = EasyMulti.Host.Open("Alice", "R", players: 4);
        room.Opened += c => code = c;
        Pump(() => code.Length > 0);

        room.Lock();

        // 等封盘在大厅可见（Lock 只是发出去，得等中继处理完）。
        EasyMultiClient watcher = EasyMulti.Client.Connect("Carol");
        IReadOnlyList<Room>? rooms = null;
        watcher.RoomsChanged += r => rooms = r;
        PumpAsking(watcher, () => rooms is { Count: 1 } && rooms[0].Started);

        // 封盘后新人进不来：走 Rejected，不走 Joined。
        var rejected = new List<string>();
        var joined = new List<string>();
        EasyMultiClient dave = EasyMulti.Client.Connect("Dave");
        dave.Rejected += rejected.Add;
        dave.Joined += joined.Add;
        dave.Join(code);
        Pump(() => rejected.Count > 0);
        Assert.Contains("game_already_started", rejected.Single());
        Assert.Empty(joined);
    }

    [Fact]
    public void StandAloneHost_RejectsPlayerWithItsName()
    {
        using var relay = new RelayHarness();
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.UdpPort });

        // 单独部署的核心后端：没有「玩家人格」。
        string code = "";
        EasyMultiHost server = EasyMulti.Host.Open("GameServer", "Ranked #1", players: 4, HostMode.StandAlone);
        server.Opened += c => code = c;
        Pump(() => code.Length > 0);

        // 与它同名的「玩家」注册合法（host 连接叫 GameServer#host，注册表不撞），
        // 但进这间房会被拒 —— 它不是服务器的玩家人格。
        var rejected = new List<string>();
        var joined = new List<string>();
        EasyMultiClient impostor = EasyMulti.Client.Connect("GameServer");
        impostor.Rejected += rejected.Add;
        impostor.Joined += joined.Add;
        impostor.Join(code);
        Pump(() => rejected.Count > 0);
        Assert.Contains("name_reserved", rejected.Single());
        Assert.Empty(joined);

        // 正常玩家不受影响。
        var ok = new List<string>();
        EasyMultiClient bob = EasyMulti.Client.Connect("Bob");
        bob.Joined += ok.Add;
        bob.Join(code);
        Pump(() => ok.Count == 1);
    }

    [Fact]
    public void Kick_SendsPlayerBackToLobby()
    {
        using var relay = new RelayHarness();
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.UdpPort });

        string code = "";
        EasyMultiHost room = EasyMulti.Host.Open("Alice", "R", players: 4);
        room.Opened += c => code = c;
        Pump(() => code.Length > 0);

        IReadOnlyList<string>? seats = null;
        var meLeft = false;
        room.PlayersChanged += s => seats = s;
        EasyMultiClient me = EasyMulti.Client.Connect("Alice");
        me.Left += () => meLeft = true;
        me.Join(code);
        Pump(() => seats is { Count: 1 });

        // 房主踢的是「玩家 Alice」，不影响自己这条 host 连接。
        room.Kick("Alice");
        Pump(() => meLeft);
        Pump(() => seats is { Count: 0 });
        Assert.Null(me.RoomCode);
    }

    [Fact]
    public void HostDropAndReturn_ReachClientFacade()
    {
        using var relay = new RelayHarness();
        // WebSocket：TCP 一断中继立刻知道；UDP 掉线要等 idle 超时，测试等不起。
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.WsPort, Transport = EasyMultiTransport.Ws });

        // 房主用低层会话扮演（要模拟的是「断线不告别」，门面 Close 是体面解散）。
        var host = RelaySession.CreateWebSocket(new SessionConfig(Token, Game, "Alice#host"));
        string code = "";
        host.RoomCreated += c => code = c;
        host.Connect("127.0.0.1", relay.WsPort);
        host.CreateRoom("R");
        Pump(() => code.Length > 0, host);

        var joined = false;
        EasyMultiClient me = EasyMulti.Client.Connect("Bob");
        me.Joined += _ => joined = true;
        me.Join(code);
        Pump(() => joined, host);

        var dropped = false;
        me.HostDropped += () => dropped = true;
        host.Dispose();
        Pump(() => dropped);

        var back = false;
        me.HostBack += () => back = true;
        var host2 = RelaySession.CreateWebSocket(new SessionConfig(Token, Game, "Alice#host"));
        host2.Connect("127.0.0.1", relay.WsPort);
        host2.JoinRoom(code);
        Pump(() => back, host2);

        host2.Dispose();
    }

    [Fact]
    public void PlayerDropAndReturn_ReachHostFacade()
    {
        using var relay = new RelayHarness();
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.WsPort, Transport = EasyMultiTransport.Ws });

        string code = "";
        EasyMultiHost room = EasyMulti.Host.Open("Alice", "R", players: 4);
        room.Opened += c => code = c;
        Pump(() => code.Length > 0);

        var bob = RelaySession.CreateWebSocket(new SessionConfig(Token, Game, "Bob"));
        bob.Connect("127.0.0.1", relay.WsPort);
        bob.JoinRoom(code);
        Pump(() => bob.State == SessionState.InRoom, bob);

        var dropped = new List<string>();
        room.PlayerDropped += dropped.Add;
        bob.Dispose();
        Pump(() => dropped.Contains("Bob"));

        var back = new List<string>();
        room.PlayerBack += back.Add;
        var bob2 = RelaySession.CreateWebSocket(new SessionConfig(Token, Game, "Bob"));
        bob2.Connect("127.0.0.1", relay.WsPort);
        bob2.JoinRoom(code);
        Pump(() => back.Contains("Bob"), bob2);

        bob2.Dispose();
    }

    [Fact]
    public void RejectedAndDisconnected_AreSeparateChannels()
    {
        // Rejected：请求被拒（这里是错 token），连接层面没断。
        using (var relay = new RelayHarness())
        {
            EasyMulti.Init(new() { Token = "wrong-token", GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.UdpPort });
            var rejected = new List<string>();
            EasyMultiClient eve = EasyMulti.Client.Connect("Eve");
            eve.Rejected += rejected.Add;
            Pump(() => rejected.Count > 0);
            Assert.Contains("bad_token", rejected[0]);
            EasyMulti.Shutdown();
        }

        // Disconnected：中继整个没了，走的是另一个频道，Rejected 保持安静。
        var relay2 = new RelayHarness();
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay2.WsPort, Transport = EasyMultiTransport.Ws });
        var rejected2 = new List<string>();
        var disconnected = new List<string>();
        EasyMultiClient frank = EasyMulti.Client.Connect("Frank");
        frank.Rejected += rejected2.Add;
        frank.Disconnected += disconnected.Add;
        Pump(() => frank.Connected);

        relay2.Dispose();
        Pump(() => disconnected.Count > 0);
        Assert.Empty(rejected2);
        Assert.False(frank.Connected);
    }

    [Fact]
    public void Connect_BeforeInit_FailsFast()
    {
        // 静态配置可能被同类里先跑的用例写过 —— 显式抹掉，还原「没 Init 过」的状态再验。
        EasyMulti.Init(new());
        Assert.Throws<InvalidOperationException>(() => EasyMulti.Client.Connect("Alice"));
        Assert.Throws<InvalidOperationException>(() => EasyMulti.Host.Open("Alice", "R", 4));
    }

    [Fact]
    public void HostClose_DisbandsRoom_AndCanOpenAgain()
    {
        using var relay = new RelayHarness();
        EasyMulti.Init(new() { Token = Token, GameId = Game, RelayHost = "127.0.0.1", RelayPort = relay.UdpPort });

        string code = "";
        EasyMultiHost first = EasyMulti.Host.Open("Alice", "R", players: 4);
        first.Opened += c => code = c;
        Pump(() => code.Length > 0);

        var meLeft = false;
        EasyMultiClient me = EasyMulti.Client.Connect("Alice");
        me.Left += () => meLeft = true;
        me.Join(code);
        Pump(() => me.RoomCode == code);

        // Close = 解散：房里的玩家被送回大厅。
        first.Close();
        Pump(() => meLeft);
        Assert.Null(me.RoomCode);

        // 再开一间就是再 Open 一个（新连接新房间）。
        string code2 = "";
        EasyMultiHost second = EasyMulti.Host.Open("Alice", "R2", players: 2);
        second.Opened += c => code2 = c;
        Pump(() => code2.Length > 0);
        Assert.NotEqual(code, code2);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Pump(Func<bool> done, params RelaySession[] raw) =>
        Pump(done, 5000, raw);

    private static void Pump(Func<bool> done, int timeoutMs, params RelaySession[] raw)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            EasyMulti.Poll();
            foreach (RelaySession session in raw) session.Poll();
            Thread.Sleep(5);
        }

        Assert.True(done(), $"条件在 {timeoutMs}ms 内未满足");
    }

    /// <summary>边等边问 —— 房间列表是「问才有」的，等它变化必须自己周期性 RefreshRooms。</summary>
    private static void PumpAsking(EasyMultiClient asker, Func<bool> done, params RelaySession[] raw)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < 5000)
        {
            asker.RefreshRooms();
            var round = Stopwatch.StartNew();
            while (!done() && round.ElapsedMilliseconds < 200)
            {
                EasyMulti.Poll();
                foreach (RelaySession session in raw) session.Poll();
                Thread.Sleep(5);
            }
        }

        Assert.True(done(), "条件在 5000ms 内未满足（已周期性 RefreshRooms）");
    }
}
