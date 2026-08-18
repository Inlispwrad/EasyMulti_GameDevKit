#nullable enable

using System;
using System.Collections.Generic;

namespace EasyMultiNet
{
    /// <summary>
    /// 房主。开房、管人、跑核心逻辑 —— 同样分成<b>动作</b>和<b>频道</b>两组。
    /// <para>
    /// Host <b>不是玩家</b>：它是一条专门的连接，不在玩家名单里、不占玩家席位。
    /// 「自己开房自己玩」＝先开好 Host，再用普通 <see cref="EasyMultiClient"/> 加进去；
    /// 想改独立服务器，把 Host 这半边挪到另一个进程即可，玩家代码一行不改。
    /// </para>
    /// <para>由 <see cref="EasyMulti.Host.Open"/> 开出来（一条连接＝一间房，开房就是创建它）；
    /// 事件全部在 <see cref="EasyMulti.Poll"/> 里、同一个线程上回调。</para>
    /// </summary>
    public sealed class EasyMultiHost
    {
        private readonly RelaySession _session;
        private readonly Action _close;
        private readonly PayloadRouter _router = new PayloadRouter();

        internal EasyMultiHost(RelaySession session, Action close)
        {
            _session = session;
            _close = close;
            _session.RoomCreated        += code         => Opened?.Invoke(code);
            _session.RoomPlayersChanged += seats        => PlayersChanged?.Invoke(seats);
            _session.PlayerDisconnected += name         => PlayerDropped?.Invoke(name);
            _session.PlayerReconnected  += name         => PlayerBack?.Invoke(name);
            _session.GameDataReceived   += (from, data) => _router.Dispatch(from, data);
            _session.Rejected           += reason       => Rejected?.Invoke(reason);
            _session.Disconnected       += reason       => Disconnected?.Invoke(reason);
        }

        // ── 动作：你调用它 ───────────────────────────────────────────────

        /// <summary>只发给某一个玩家（补发局面、私密信息、发牌之类）。<b>T 就是消息通道</b>。</summary>
        public void Send<T>(string playerId, T value) => _session.SendGameData(PayloadRouter.EncodeMessage(value), to: playerId);

        /// <summary>发给房间里所有玩家。<b>T 就是消息通道</b>：对端 Receive 了同一个 T 才收得到。</summary>
        public void Broadcast<T>(T value) => _session.SendGameData(PayloadRouter.EncodeMessage(value));

        /// <summary>
        /// 订一条类型通道：玩家发来的 <typeparamref name="T"/> 消息从这里进 (fromId, value)。
        /// <b>核心逻辑从这里开始。</b>没订阅的类型静默丢弃；同一 T 多次订阅＝叠加。
        /// </summary>
        public void Receive<T>(Action<string, T> handler) => _router.Register(handler);

        /// <summary>把某个玩家踢出房间。中继只保名单不做超时，掉线玩家什么时候清走由你决定。</summary>
        public void Kick(string player) => _session.Kick(player);

        /// <summary>
        /// 封盘：房间在大厅里标成进行中，新人不能再进。<b>只关房间入口</b>，
        /// 不启动任何游戏逻辑 —— 什么时候算开局是你的事。
        /// </summary>
        public void Lock() => _session.StartGame();

        /// <summary>
        /// 解散房间并断开：房间里的玩家全部被送回大厅（各自收到 Left），房间销毁。
        /// 想让房间「等你回来」就别调它 —— 直接断线的话座位保留，玩家收到的是 HostDropped。
        /// </summary>
        public void Close()
        {
            _session.LeaveRoom(); // host 的 LEAVE_ROOM = 解散
            _close();
        }

        // ── 频道：你订阅它，消息推给你 ─────────────────────────────────────

        /// <summary>
        /// 房间开好了，参数是房码。把它给玩家，他们就能进来。
        /// <para>
        /// Host 没有「在大厅」这个状态 —— <see cref="EasyMulti.Host.Open"/> 连上就直接开房
        /// （开房指令在注册完成前自动排队），这个事件就是你要等的第一个信号。
        /// </para>
        /// </summary>
        public event Action<string>? Opened;

        /// <summary>房间里的玩家名单变了。房主不是玩家，名单里从来没有它。</summary>
        public event Action<IReadOnlyList<string>>? PlayersChanged;

        /// <summary>某个玩家掉线了，座位仍保留（名单不变）。要不要清走他，用 <see cref="Kick"/> 说了算。</summary>
        public event Action<string>? PlayerDropped;

        /// <summary>掉线的玩家重连坐回来了。在这里给他补发局面。</summary>
        public event Action<string>? PlayerBack;

        /// <summary>某个请求被中继拒绝了。参数是原因。</summary>
        public event Action<string>? Rejected;

        /// <summary>与中继的连接断了。参数是原因。</summary>
        public event Action<string>? Disconnected;
    }
}
