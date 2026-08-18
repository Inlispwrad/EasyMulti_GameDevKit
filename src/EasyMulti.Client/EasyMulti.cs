#nullable enable

using System;
using System.Collections.Generic;

namespace EasyMultiNet
{
    /// <summary>Host 的部署形态，<see cref="EasyMulti.Host.Open"/> 的参数。</summary>
    public enum HostMode
    {
        /// <summary>默认：某个玩家顺手开的房。同名玩家（就是房主本人）可以进来。</summary>
        Default,

        /// <summary>
        /// 单独部署的 host（核心后端/专服）：它没有「玩家人格」，中继会拒绝与它同名的
        /// 玩家进房（Rejected: name_reserved）。
        /// </summary>
        StandAlone,
    }

    /// <summary>连中继走哪种传输，<see cref="EasyMultiConfig.Transport"/> 的取值。</summary>
    public enum EasyMultiTransport
    {
        /// <summary>默认。自带 ack/重传/分片，延迟最低；要求中继的 UDP 端口能直连。</summary>
        Udp,

        /// <summary>明文 WebSocket。UDP 被防火墙挡住、或部署平台根本不给 UDP 端口时用。</summary>
        Ws,

        /// <summary>
        /// TLS WebSocket。**浏览器 / WASM 里的 HTTPS 页面只能用这个** —— 明文会被混合内容策略
        /// 拦掉。中继本身只跑明文，TLS 由它前面的反向代理终结（见 docs/DEPLOY.md）。
        /// </summary>
        Wss,
    }

    /// <summary>
    /// 连接配置 —— <see cref="EasyMulti.Init"/> 的唯一参数，原地 new 就行：
    /// <code>
    /// EasyMulti.Init(new()
    /// {
    ///     Token     = "你的token",
    ///     GameId    = "my-game",
    ///     RelayHost = "你的服务器地址",
    ///     RelayPort = 7777,
    ///     Codec     = new MemoryPackCodec(),
    /// });
    /// </code>
    /// </summary>
    public sealed class EasyMultiConfig
    {
        /// <summary>中继的共享密钥。挡爬虫用的，不是安全边界（见 docs/DEPLOY.md）。</summary>
        public string Token { get; set; } = "";

        /// <summary>房间的命名空间：不同 gameId 的房间互相看不见。</summary>
        public string GameId { get; set; } = "";

        /// <summary>中继地址，域名或 IP，**不带协议头**（协议由 <see cref="Transport"/> 决定）。</summary>
        public string RelayHost { get; set; } = "";

        /// <summary>中继端口。反代后的 wss 一般填 443。</summary>
        public int RelayPort { get; set; } = 7777;

        /// <summary>
        /// 走哪种传输。浏览器 / WASM 里的 HTTPS 页面必须用 <see cref="EasyMultiTransport.Wss"/>。
        /// 三种传输的玩家可以在同一间房里。
        /// </summary>
        public EasyMultiTransport Transport { get; set; } = EasyMultiTransport.Udp;

        /// <summary>WebSocket 专用：反向代理把中继挂在哪个路径下（如 <c>"/em"</c>）。UDP 忽略。</summary>
        public string Path { get; set; } = "/";

        /// <summary>
        /// 对局数据 body 的编解码器（推荐 MemoryPack，8 行拷贝件见 <see cref="IPayloadCodec"/> 文档）。
        /// 不填就用不了 <c>Send&lt;T&gt;</c> / <c>Receive&lt;T&gt;</c>（fail-fast）。
        /// </summary>
        public IPayloadCodec? Codec { get; set; }

        /// <summary>
        /// 自带传输实现。填了就盖过 <see cref="Transport"/>。典型场景是 Unity WebGL 这类运行时 ——
        /// BCL 的 <c>ClientWebSocket</c> 在那里用不了，得桥接宿主的 JS WebSocket。
        /// </summary>
        public Func<IClientTransport>? TransportFactory { get; set; }
    }

    /// <summary>
    /// EasyMulti 的全局门面 —— 产品本身。傻瓜式四步：
    /// <see cref="Init"/> 一次（**只写入配置数据，不建任何连接**）→
    /// <see cref="Client.Connect"/> / <see cref="Host.Open"/> 拿到角色实例 →
    /// 每帧 <see cref="Poll"/> → 退出时 <see cref="Shutdown"/>。
    /// <para>
    /// 所有连接都在 Poll 里、同一个线程上回调事件 —— 游戏侧不需要任何锁。
    /// 典型做法是把 Poll 挂在一个常驻节点（Godot autoload / Unity 常驻 MonoBehaviour）上。
    /// </para>
    /// </summary>
    public static class EasyMulti
    {
        /// <summary>房主那条连接的名字后缀，用来和同名玩家区分开。协议约定，见 <see cref="Protocol.RelayNaming"/>。</summary>
        public const string HostSuffix = Protocol.RelayNaming.HostSuffix;

        /// <summary>当前挂着的编解码器。写入点只有 <see cref="Init"/>（<see cref="EasyMultiConfig.Codec"/>）。</summary>
        public static IPayloadCodec? Codec { get; private set; }

        private static string _token = "";
        private static string _gameId = "";
        private static string _relayHost = "";
        private static int _relayPort;
        private static EasyMultiTransport _transport;
        private static string _path = "/";
        private static Func<IClientTransport>? _newTransport;
        private static readonly List<RelaySession> _sessions = new List<RelaySession>();
        private static int _boundThreadId = -1;

        /// <summary>
        /// 写入连接要用的配置。只存数据，此刻不会碰网络；重复调用就是整份覆盖。
        /// 配置对象原地 new 即可，写法见 <see cref="EasyMultiConfig"/>。
        /// </summary>
        public static void Init(EasyMultiConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            _token = config.Token;
            _gameId = config.GameId;
            _relayHost = config.RelayHost;
            _relayPort = config.RelayPort;
            _transport = config.Transport;
            _path = config.Path;
            _newTransport = config.TransportFactory;
            Codec = config.Codec;
        }

        /// <summary>玩家侧的 API 入口。</summary>
        public static class Client
        {
            /// <summary>
            /// 以 <paramref name="playerId"/> 连上中继，返回玩家实例。
            /// 拿到就能订频道、调动作 —— 动作不必等连上（注册前自动排队）。
            /// </summary>
            public static EasyMultiClient Connect(string playerId)
            {
                RelaySession session = OpenSession(playerId);
                return new EasyMultiClient(session, () => Drop(session));
            }
        }

        /// <summary>房主侧的 API 入口。</summary>
        public static class Host
        {
            /// <summary>
            /// 以 <paramref name="name"/> 连上中继并开一间房，返回房主实例（一条连接＝一间房）。
            /// 房码走 <see cref="EasyMultiNet.Host.Opened"/> 回来。
            /// <para>
            /// 名字和某个玩家一样＝那个玩家顺手开的房（连接注册名自动带 <see cref="HostSuffix"/>，
            /// 两条连接互不打架）。单独部署的核心后端传 <see cref="HostMode.StandAlone"/>，
            /// 中继会拒绝与它同名的玩家进房。
            /// </para>
            /// </summary>
            /// <param name="players">要坐几个玩家。房主不占玩家席位，要几个就填几个。</param>
            public static EasyMultiHost Open(string name, string title, int players, HostMode mode = HostMode.Default)
            {
                RelaySession session = OpenSession(name + HostSuffix);
                session.CreateRoom(title, maxPlayers: players, dedicated: mode == HostMode.StandAlone);
                return new EasyMultiHost(session, () => Drop(session));
            }
        }

        /// <summary>
        /// SDK 的心跳：驱动所有连接的收发，并把攒下的网络事件派发出来 —— 你订阅的每一个
        /// 频道（Joined / Received / PlayersChanged / …）都<b>只会</b>在这句话执行期间、
        /// 在调用它的这个线程上触发。
        /// <para>
        /// <b>放进游戏主循环，每帧调一次</b>：Godot 挂 <c>_Process</c>、Unity 挂 <c>Update</c>、
        /// 专服进程就 <c>while (true) { EasyMulti.Poll(); Thread.Sleep(1); }</c>。
        /// 网络 I/O 本身在后台线程跑，收到的消息先进队列，Poll 把它们搬到你的线程上再回调
        /// —— 所以回调里直接改 UI、改游戏状态都是安全的，不需要任何锁或跨线程调度。
        /// </para>
        /// <para>
        /// 两个直接后果：<b>不调 Poll 就什么都不会发生</b>（事件不派发，UDP 的 ack/重传也
        /// 停摆，连接会假死）；调用频率就是你的事件延迟（每帧 ≈16ms 足够，追求低延迟就调密些）。
        /// </para>
        /// </summary>
        public static void Poll()
        {
            BindThread();

            // 倒序：回调里 Host.Close / Client.Disconnect 会把自己从列表移走，倒着走不会漏泵别人。
            for (int i = _sessions.Count - 1; i >= 0; i--)
            {
                _sessions[i].Poll();
            }
        }

        /// <summary>断开并释放所有连接。配置保留，之后可以直接再 Connect / Open（换线程也从这之后才行）。</summary>
        public static void Shutdown()
        {
            BindThread();
            foreach (RelaySession session in _sessions) session.Dispose();
            _sessions.Clear();
            _boundThreadId = -1; // 解除线程绑定，允许在新线程上重新开始
        }

        private static RelaySession OpenSession(string name)
        {
            BindThread();
            if (string.IsNullOrWhiteSpace(_token)
                || string.IsNullOrWhiteSpace(_gameId)
                || string.IsNullOrWhiteSpace(_relayHost))
            {
                throw new InvalidOperationException(
                    "先填好配置：EasyMulti.Init(new() { Token = …, GameId = …, RelayHost = …, RelayPort = 7777 })");
            }

            var session = new RelaySession(
                new SessionConfig(_token, _gameId, name),
                _newTransport?.Invoke() ?? NewTransport());
            _sessions.Add(session);
            session.Connect(_relayHost, _relayPort);
            return session;
        }

        private static IClientTransport NewTransport() => _transport switch
        {
            EasyMultiTransport.Ws => new WebSocketClientTransport(secure: false, path: _path),
            EasyMultiTransport.Wss => new WebSocketClientTransport(secure: true, path: _path),
            _ => new UdpClientTransport(),
        };

        private static void Drop(RelaySession session)
        {
            BindThread();
            _sessions.Remove(session);
            session.Dispose();
        }

        /// <summary>
        /// 第一个调用 Connect / Open / Poll 的线程成为「SDK 线程」，之后换线程调用直接报错。
        /// 这不是洁癖：事件回调发生在 Poll 的线程上，连接状态也只在这个线程上读写 ——
        /// 固定一个线程是「回调里能直接改 UI / 游戏状态、全程无锁」的前提。
        /// </summary>
        private static void BindThread()
        {
            int current = Environment.CurrentManagedThreadId;
            if (_boundThreadId < 0)
            {
                _boundThreadId = current;
            }
            else if (_boundThreadId != current)
            {
                throw new InvalidOperationException(
                    "EasyMulti 的 Connect / Open / Poll / Close / Shutdown 必须固定在同一个线程上使用"
                    + "（通常是游戏主循环所在的线程）—— 事件回调就发生在这个线程上。"
                    + "别在 Task.Run / async 续体 / 后台线程里调它；要换线程，先在旧线程 Shutdown()。");
            }
        }
    }
}
