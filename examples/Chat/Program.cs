#nullable enable

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using EasyMultiNet;
using EasyMultiNet.Protocol;

// ── EasyMulti Chat / 延迟测试 ────────────────────────────────────────────────
// 终端版聊天室。默认走 UDP；用 --transport ws 可切 WebSocket。
//
// 应用层消息（跑在 GAME_DATA.data 里，纯 JSON，中继不解析）：
//   chat: {"t":"chat","name":"...","text":"...","at":<ms>}    广播
//   ping: {"t":"ping","id":N,"sent":<ms>}                      广播，每 1s 一次
//   pong: {"t":"pong","id":N,"sent":<ms>}                      定向回给 pinger
//
// 用法：
//   dotnet run --project examples/Chat -- --mode host   --name Host   --transport udp
//   dotnet run --project examples/Chat -- --mode client --name Alice  --transport ws --room <CODE>
//   加 --bench 200 表示跑基准：自动 ping/pong，测满 200 个 pong 后打印 RTT 统计并退出。

internal static class Program
{
    private static readonly JsonSerializerOptions AppJson = new(JsonSerializerDefaults.Web);

    private static int Main(string[] args)
    {
        var opts = Options.Parse(args);

        var config = new SessionConfig(opts.Token, opts.Game, opts.Name);
        var client = opts.Transport == "udp"
            ? RelaySession.CreateUdp(config)
            : RelaySession.CreateWebSocket(config);

        var latency = new Dictionary<string, long>();          // peer -> 最近一次 RTT(ms)
        var benchRtts = new List<long>();                      // 基准模式收集的样本
        var stdinQueue = new ConcurrentQueue<string>();
        int benchPongs = 0;
        int pingId = 0;

        client.Rejected += reason => Log("被拒：" + reason);
        client.Disconnected += reason => Log("断线：" + reason);
        // 房间列表是「问才有」的，服务器不会主动送，所以这里自己问一次。
        client.Registered += () => { Log($"{opts.Name} 已注册"); client.RefreshRooms(); };
        client.RoomListChanged += rooms => Log($"大厅：{rooms.Count} 个房间");
        client.RoomPlayersChanged += players => Log("成员：" + string.Join(", ", players));

        if (opts.Mode == "host")
        {
            client.RoomCreated += code =>
            {
                Console.WriteLine($"[Chat] ROOM_CODE={code}");
                Console.Out.Flush();
            };
            client.Registered += () => client.CreateRoom("Chat", 8);
        }
        else
        {
            client.RoomJoined += code => Log($"已加入房间 {code}");
            client.Registered += () => client.JoinRoom(opts.Room);
        }

        client.GameDataReceived += (from, raw) =>
        {
            string data = Encoding.UTF8.GetString(raw); // 应用层协议：UTF8(JSON)
            if (!TryReadApp(data, out string? t, out JsonDocument? doc)) return;

            switch (t)
            {
                case "chat":
                    Console.WriteLine($"[{from}] {doc!.RootElement.GetProperty("text").GetString()}");
                    break;

                case "ping":
                {
                    int id = doc!.RootElement.GetProperty("id").GetInt32();
                    long sent = doc.RootElement.GetProperty("sent").GetInt64();
                    client.SendGameData(Bytes(new { t = "pong", id, sent }), to: from);
                    break;
                }

                case "pong":
                {
                    long sent = doc!.RootElement.GetProperty("sent").GetInt64();
                    long rtt = NowMs() - sent;
                    latency[from] = rtt;
                    if (opts.BenchCount > 0)
                    {
                        benchRtts.Add(rtt);
                        if (++benchPongs >= opts.BenchCount)
                        {
                            PrintBench(benchRtts, opts.Transport);
                            Environment.Exit(0);
                        }
                    }
                    break;
                }
            }

            doc?.Dispose();
        };

        Log($"连接中继 127.0.0.1:{opts.Port}（{opts.Transport}）");
        client.Connect("127.0.0.1", opts.Port);

        if (opts.BenchCount == 0)
        {
            _ = Task.Run(() =>
            {
                while (true)
                {
                    string? line = Console.ReadLine();
                    if (line == null) return;
                    stdinQueue.Enqueue(line);
                }
            });
        }

        var nextPing = NowMs();
        var nextLatPrint = NowMs();
        while (true)
        {
            client.Poll();

            while (stdinQueue.TryDequeue(out string? line))
            {
                if (line == "/quit") return 0;
                client.SendGameData(Bytes(new { t = "chat", name = opts.Name, text = line, at = NowMs() }));
            }

            long now = NowMs();
            if (client.State == SessionState.InRoom && now >= nextPing)
            {
                client.SendGameData(Bytes(new { t = "ping", id = ++pingId, sent = now }));
                nextPing = now + opts.PingInterval;
            }

            if (opts.BenchCount == 0 && now >= nextLatPrint)
            {
                if (latency.Count > 0)
                {
                    string lat = string.Join("  ", latency.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}ms"));
                    Log("RTT: " + lat);
                }
                nextLatPrint = now + 2000;
            }

            // Thread.Sleep(1) resolves to ~1 ms on this platform, keeping the pong path low-latency.
            // (SpinWait.SpinUntil and SemaphoreSlim.Wait(1) actually round up to ~10 ms on macOS.)
            Thread.Sleep(1);
        }
    }

    private static void PrintBench(List<long> rtts, string transport)
    {
        rtts.Sort();
        int n = rtts.Count;
        double avg = rtts.Average();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            transport,
            samples = n,
            min = rtts[0],
            avg = Math.Round(avg * 10) / 10,
            p50 = rtts[(int)Math.Floor(n * 0.5)],
            p95 = rtts[(int)Math.Floor(n * 0.95)],
            max = rtts[n - 1],
        }));
        Console.Out.Flush();
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string Encode(object o) => JsonSerializer.Serialize(o, AppJson);

    private static byte[] Bytes(object o) => Encoding.UTF8.GetBytes(Encode(o));

    private static bool TryReadApp(string data, out string? t, out JsonDocument? doc)
    {
        t = null;
        doc = null;
        try
        {
            doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("t", out JsonElement tEl)) return false;
            t = tEl.GetString();
            return t != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
}

internal static class Options
{
    public static Parsed Parse(string[] args)
    {
        string mode = "client", name = "Host", transport = "udp", room = "", token = "demo-token", game = "chat";
        int port = 7777, bench = 0, pingInterval = 1000;

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
                case "--port": port = int.Parse(v); break;
                case "--bench": bench = int.Parse(v); break;
                case "--ping-interval": pingInterval = int.Parse(v); break;
                default: throw new ArgumentException($"不认识的参数 {args[i]}");
            }
        }

        if (mode is not ("host" or "client")) throw new ArgumentException("--mode 必须是 host 或 client");
        if (transport is not ("udp" or "ws")) throw new ArgumentException("--transport 必须是 udp 或 ws");
        if (mode == "client" && room.Length == 0) throw new ArgumentException("client 模式需要 --room <房码>");

        return new Parsed(mode, name, transport, room, token, game, port, bench, pingInterval);
    }

    public sealed record Parsed(string Mode, string Name, string Transport, string Room, string Token, string Game, int Port, int BenchCount, int PingInterval);
}
