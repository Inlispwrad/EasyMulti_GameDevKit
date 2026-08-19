#nullable enable

using System.Collections.Concurrent;
using EasyMultiNet;

// ── EasyMulti 终端聊天室 ─────────────────────────────────────────────────────
//
// 部署完中继之后拿它验证：能进同一个房间聊上天，就说明连接、鉴权、转发全都通了。
// 也是 SDK 推荐用法的最小完整示例 —— 全局门面 + 类型通道，和 samples/ChatGodot
// 是同一套写法、同一份消息定义，两者可以互通。
//
// **token 从命令行传入，不写死在代码里。** 你的密钥不该躺在任何会被提交的文件里。
//
//   # 开一间房（顺便自己也进去聊）
//   dotnet run --project examples/ChatCli -- --mode create --name Alice \
//       --relay-host 你的服务器IP --token 你的token
//
//   # 别人拿房码进来
//   dotnet run --project examples/ChatCli -- --mode join --name Bob --room ABC123 \
//       --relay-host 你的服务器IP --token 你的token
//
// 输入一行回车即发送，输入 /quit 退出。

return App.Run(args);

internal static class App
{
    public static int Run(string[] args)
    {
        Options opts;
        try
        {
            opts = Options.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine("参数有误：" + e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        EasyMulti.Init(new()
        {
            Token     = opts.Token,
            GameId    = opts.Game,
            RelayHost = opts.RelayHost,
            RelayPort = opts.RelayPort,
            Transport = opts.Transport,
            Codec     = new MemoryPackCodec(),
        });

        Console.WriteLine($"连接 {opts.RelayHost}:{opts.RelayPort}（{opts.Transport}），身份 {opts.Name}");

        // 开房的人同时扮演两个角色：一条 host 连接跑权威逻辑，外加一条普通玩家连接
        // 参与聊天。这样玩家侧的代码永远不需要判断「我是不是房主」——想改成独立
        // 部署的专服，把 ChatCore 那半边挪进另一个进程就行，客户端一个字都不用改。
        ChatCore? core = null;
        if (opts.Mode == "create")
        {
            core = new ChatCore(opts.Name, opts.HistoryLines);
        }

        EasyMultiClient me = EasyMulti.Client.Connect(opts.Name);
        var joined = false;

        me.Joined += code =>
        {
            joined = true;
            Console.WriteLine($"── 已进入房间 {code} ──");
            if (opts.Mode == "create")
            {
                Console.WriteLine($"   把房码 {code} 发给其他人，他们用 --mode join --room {code} 进来");
            }

            Console.WriteLine("   输入内容回车发送，/quit 退出");
        };

        me.Receive<SayMsg>(m => Console.WriteLine($"[{m.Seq,3}] {m.Who}: {m.Text}"));
        me.Receive<WhoMsg>(m => Console.WriteLine($"── 房间里的人：{string.Join("、", m.Players)} ──"));
        me.Left += () => Console.WriteLine("── 已离开房间 ──");
        me.HostDropped += () => Console.WriteLine("── 房主掉线了，等他回来 ──");
        me.HostBack += () => Console.WriteLine("── 房主回来了 ──");
        me.Rejected += why => Console.Error.WriteLine("被拒：" + why);
        me.Disconnected += why => Console.Error.WriteLine("断线：" + why);

        // 房码：开房的从 host 那边拿，加入的直接用命令行给的。
        // 动作都不必等连上 —— 没注册完 SDK 会先攒着，注册成功后按序补发。
        if (core != null) core.Opened += code => me.Join(code);
        else me.Join(opts.Room);

        // Console.ReadLine 会阻塞，而 SDK 的所有调用必须待在同一个线程上（就是下面这个
        // 跑 Poll 的循环）。所以键盘在后台线程读，读到的行丢进队列，由主循环取出来发。
        var typed = new ConcurrentQueue<string>();
        var quitting = false;
        var keyboard = new Thread(() =>
        {
            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                if (line == "/quit") { quitting = true; return; }
                if (line.Length > 0) typed.Enqueue(line);
            }
        })
        { IsBackground = true };
        keyboard.Start();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quitting = true; };

        while (!quitting)
        {
            EasyMulti.Poll();

            while (joined && typed.TryDequeue(out string? line))
            {
                me.Send(line); // T=string 一条通道；host 定序后以 SayMsg 广播回来
            }

            Thread.Sleep(16);
        }

        Console.WriteLine("再见。");
        EasyMulti.Shutdown();
        return 0;
    }
}

/// <summary>
/// 房主侧的权威逻辑。它决定发言的顺序、维护名单、给新来的人补发历史 ——
/// 中继完全不参与这些，它只负责把字节送到。
/// </summary>
internal sealed class ChatCore
{
    private const int MaxTextLength = 500;

    private readonly List<SayMsg> _backlog = new();
    private readonly HashSet<string> _seen = new();
    private readonly int _keep;
    private readonly EasyMultiHost _room;
    private int _seq;

    public ChatCore(string ownerName, int keep)
    {
        _keep = keep;
        _room = EasyMulti.Host.Open(ownerName, $"{ownerName} 的聊天室", players: 16);

        _room.Opened += code => Opened?.Invoke(code);
        _room.PlayersChanged += OnPlayersChanged;
        _room.Receive<string>(OnPlayerSaid);

        _room.Rejected += why => Console.Error.WriteLine("[host] 被拒：" + why);
        _room.Disconnected += why => Console.Error.WriteLine("[host] 断线：" + why);
    }

    public event Action<string>? Opened;

    /// <summary>玩家发来一句话：校验、定序，然后广播给所有人（包括他自己）。</summary>
    private void OnPlayerSaid(string from, string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > MaxTextLength) return; // 只有权威侧的校验算数

        var said = new SayMsg(++_seq, from, text);
        _backlog.Add(said);
        if (_backlog.Count > _keep) _backlog.RemoveAt(0);
        _room.Broadcast(said);
    }

    /// <summary>
    /// 人员变动：名单广播给所有人；对**刚进来**的那几个，再单独补一份最近的对话。
    /// host 没有「某人进来了」这个事件 —— 新人是从名单的差异里认出来的。
    /// </summary>
    private void OnPlayersChanged(IReadOnlyList<string> players)
    {
        _room.Broadcast(new WhoMsg(++_seq, players.ToArray()));

        var current = new HashSet<string>(players);
        _seen.RemoveWhere(name => !current.Contains(name)); // 走了的人，下次再来算新人

        foreach (string name in players)
        {
            if (!_seen.Add(name)) continue; // 老面孔，不用补
            foreach (SayMsg say in _backlog) _room.Send(name, say);
        }
    }
}

internal sealed record Options(
    string Mode, string Name, string Room, string Token, string Game,
    string RelayHost, int RelayPort, EasyMultiTransport Transport, int HistoryLines)
{
    public const string Usage = """
        用法：
          --mode create|join   开一间房，还是加入已有的房间（必填）
          --name <你的名字>    在这个房间里的身份（必填）
          --token <token>      中继的共享密钥（必填）
          --room <房码>        --mode join 时必填
          --relay-host <地址>  中继地址，默认 127.0.0.1
          --relay-port <端口>  默认 7777
          --transport udp|ws|wss  默认 udp
          --history <行数>     给新人补发多少条历史，默认 20
        """;

    public static Options Parse(string[] args)
    {
        string mode = "", name = "", room = "", token = "", game = "chat-cli", host = "127.0.0.1";
        string transport = "udp";
        int port = 7777, history = 20;

        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} 后面缺一个值");
            string v = args[i + 1];
            switch (args[i])
            {
                case "--mode": mode = v; break;
                case "--name": name = v; break;
                case "--room": room = v; break;
                case "--token": token = v; break;
                case "--game": game = v; break;
                case "--relay-host": host = v; break;
                case "--relay-port": port = int.Parse(v); break;
                case "--transport": transport = v; break;
                case "--history": history = int.Parse(v); break;
                default: throw new ArgumentException($"不认识的参数 {args[i]}");
            }
        }

        if (mode is not ("create" or "join")) throw new ArgumentException("--mode 必须是 create 或 join");
        if (name.Length == 0) throw new ArgumentException("--name 不能为空");
        if (token.Length == 0) throw new ArgumentException("--token 不能为空（中继没有默认密码）");
        if (mode == "join" && room.Length == 0) throw new ArgumentException("--mode join 需要 --room <房码>");

        EasyMultiTransport t = transport switch
        {
            "udp" => EasyMultiTransport.Udp,
            "ws" => EasyMultiTransport.Ws,
            "wss" => EasyMultiTransport.Wss,
            _ => throw new ArgumentException("--transport 必须是 udp、ws 或 wss"),
        };

        return new Options(mode, name, room, token, game, host, port, t, history);
    }
}
