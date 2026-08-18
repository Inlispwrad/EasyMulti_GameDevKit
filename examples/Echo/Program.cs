#nullable enable

using EasyMultiNet;
using MemoryPack;

// ── EasyMulti Echo demo ──────────────────────────────────────────────────────
// 全局门面（EasyMulti.Init → PlayerId → Client / Host → Poll）的最小用法。
// One program, two roles:
//   host   — a minimal "hostCore": opens a room, then echoes every message it
//            receives back to the sender (the authoritative loop is just this
//            echo; a real game would tick its own simulation here).
//   client — joins a room by code, sends "ping N" every two seconds and prints
//            whatever comes back.
//
// Run the relay first:
//   dotnet run --project src/EasyMulti.Relay -- --token demo-token
// Then (two terminals):
//   dotnet run --project examples/Echo -- --mode host   --name Host   --transport udp
//   dotnet run --project examples/Echo -- --mode client --name Alice  --transport ws --room <CODE>

return Run(args);

static int Run(string[] args)
{
    var opts = Options.Parse(args);

    EasyMulti.Init(new()
    {
        Token     = opts.Token,
        GameId    = opts.Game,
        RelayHost = "127.0.0.1",
        RelayPort = opts.Port,
        Transport = opts.Transport == "ws" ? EasyMultiTransport.Ws : EasyMultiTransport.Udp,
        Codec     = new MemoryPackCodec(), // 默认壳：T 即消息通道，body 走 MemoryPack
    });

    EasyMultiClient? me = null;
    if (opts.Mode == "host")
    {
        // 这是一个单独跑的 host 进程（核心后端形态），所以声明 StandAlone：
        // 没有「玩家人格」，与它同名的玩家会被中继拒之门外。
        EasyMultiHost room = EasyMulti.Host.Open(opts.Name, "Echo Room", players: 4, HostMode.StandAlone);
        room.Opened += code => Console.WriteLine($"[Echo] 房间已创建，房码 = {code}");
        room.PlayersChanged += players => Console.WriteLine("[Echo] 玩家：" + string.Join(", ", players));
        room.Receive<string>((from, text) =>
        {
            Console.WriteLine($"[Echo] Host 收到 {from}: {text}");
            // hostCore：权威循环在这里处理输入并回发结果。这里原样回显给发送者。
            room.Send(from, "echo:" + text);
        });
        room.Rejected += reason => Console.Error.WriteLine("[Echo] 被拒：" + reason);
        room.Disconnected += reason => Console.Error.WriteLine("[Echo] 断线：" + reason);
    }
    else
    {
        me = EasyMulti.Client.Connect(opts.Name);
        me.Joined += code => Console.WriteLine($"[Echo] 已加入房间 {code}");
        me.Receive<string>(text => Console.WriteLine($"[Echo] {opts.Name} 收到：{text}"));
        me.HostDropped += () => Console.WriteLine("[Echo] 房主掉线了，等他回来…");
        me.HostBack += () => Console.WriteLine("[Echo] 房主回来了");
        me.Rejected += reason => Console.Error.WriteLine("[Echo] 被拒：" + reason);
        me.Disconnected += reason => Console.Error.WriteLine("[Echo] 断线：" + reason);
        me.Join(opts.Room); // 不用等连上，注册完成后自动补发
    }

    Console.WriteLine($"[Echo] 连接中继 127.0.0.1:{opts.Port}（{opts.Transport}）");

    int ping = 0;
    var nextPing = DateTime.UtcNow;
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline)
    {
        EasyMulti.Poll();

        if (me is { RoomCode: not null } && DateTime.UtcNow >= nextPing)
        {
            me.Send($"ping {++ping}"); // T=string 一条通道，MemoryPack 原生支持
            nextPing = DateTime.UtcNow.AddSeconds(2);
        }

        Thread.Sleep(10);
    }

    Console.WriteLine("[Echo] 演示结束");
    EasyMulti.Shutdown();
    return 0;
}

/// <summary>对局数据的编解码器（SDK 零依赖，所以这 8 行住在你的工程里）。</summary>
internal sealed class MemoryPackCodec : IPayloadCodec
{
    public byte[] Encode<T>(T value) => MemoryPackSerializer.Serialize(value);

    public T Decode<T>(ReadOnlySpan<byte> body) => MemoryPackSerializer.Deserialize<T>(body)!;
}

internal static class Options
{
    public static Parsed Parse(string[] args)
    {
        string mode = "client", name = "Host", transport = "udp", room = "", token = "demo-token", game = "echo";
        int port = 7777;

        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            string v = args[i + 1];
            switch (args[i])
            {
                case "--mode": mode = v; break;
                case "--name": name = v; break;
                case "--transport": transport = v; break;
                case "--room": room = v; break;
                case "--token": token = v; break;
                case "--game": game = v; break;
                case "--relay-port": port = int.Parse(v); break;
                default: throw new ArgumentException($"不认识的参数 {args[i]}");
            }
        }

        if (mode is not ("host" or "client")) throw new ArgumentException("--mode 必须是 host 或 client");
        if (transport is not ("udp" or "ws")) throw new ArgumentException("--transport 必须是 udp 或 ws");
        if (mode == "client" && room.Length == 0) throw new ArgumentException("client 模式需要 --room <房码>");

        return new Parsed(mode, name, transport, room, token, game, port);
    }

    internal sealed record Parsed(string Mode, string Name, string Transport, string Room, string Token, string Game, int Port);
}
