#nullable enable

using EasyMulti.Client;
using EasyMulti.Protocol;

// ── EasyMulti Echo demo ──────────────────────────────────────────────────────
// One program, two roles:
//   host   — a minimal "hostCore": registers, creates a room, then echoes every
//            GAME_DATA it receives back to the sender (the authoritative loop is
//            just this echo; a real game would tick its own simulation here).
//   client — registers, joins a room by code, sends "ping N" every two seconds
//            and prints whatever comes back.
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

    var config = new EasyMultiConfig(opts.Token, opts.Game, opts.Name);
    var client = opts.Transport == "udp"
        ? EasyMultiClient.CreateUdp(config)
        : EasyMultiClient.CreateWebSocket(config);

    client.Failed += reason => Console.Error.WriteLine("[Echo] 失败：" + reason);
    client.Registered += () => Console.WriteLine($"[Echo] {opts.Name} 已注册");
    client.RoomListChanged += rooms => Console.WriteLine($"[Echo] 大厅：{rooms.Count} 个房间");

    if (opts.Mode == "host")
    {
        client.RoomCreated += code => Console.WriteLine($"[Echo] 房间已创建，房码 = {code}");
        client.GameDataReceived += (from, data) =>
        {
            Console.WriteLine($"[Echo] Host 收到 {from}: {data}");
            // hostCore：权威循环在这里处理输入并回发结果。这里原样回显给发送者。
            client.SendGameData("echo:" + data, to: from);
        };
        client.Registered += () => client.CreateRoom("Echo Room", 4);
    }
    else
    {
        client.RoomJoined += code => Console.WriteLine($"[Echo] 已加入房间 {code}");
        client.GameDataReceived += (from, data) =>
            Console.WriteLine($"[Echo] {opts.Name} 收到 {from}: {data}");
        client.Registered += () => client.JoinRoom(opts.Room);
    }

    Console.WriteLine($"[Echo] 连接中继 127.0.0.1:{opts.Port}（{opts.Transport}）");
    client.Connect("127.0.0.1", opts.Port);

    int ping = 0;
    var nextPing = DateTime.UtcNow;
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline)
    {
        client.Poll();

        if (opts.Mode == "client" && client.State == EasyMultiState.InRoom && DateTime.UtcNow >= nextPing)
        {
            client.SendGameData($"ping {++ping}", mode: DeliveryMode.Reliable);
            nextPing = DateTime.UtcNow.AddSeconds(2);
        }

        Thread.Sleep(10);
    }

    Console.WriteLine("[Echo] 演示结束");
    return 0;
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
