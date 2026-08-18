#nullable enable

using System;
using System.Collections.Generic;
using EasyMultiNet.Protocol;

namespace EasyMultiNet
{
    /// <summary>大厅里的一间房。人数和容量都只算玩家 —— 房主不是玩家，不占数。</summary>
    public readonly struct Room
    {
        public Room(string code, string name, int players, int capacity, bool started)
        {
            Code = code;
            Name = name;
            Players = players;
            Capacity = capacity;
            Started = started;
        }

        public readonly string Code;
        public readonly string Name;
        public readonly int Players;
        public readonly int Capacity;
        public readonly bool Started;
    }

    /// <summary>
    /// 玩家。进得去、出得来、能收发消息，仅此而已 —— 分成<b>动作</b>（你调用）和
    /// <b>频道</b>（你订阅，消息推给你）两组。
    /// <para>
    /// 这里<b>没有</b>开房、踢人、成员名单，连「房主叫什么名字」都不需要知道：
    /// 房间怎么管，玩家不关心；消息发到哪儿去，由 SDK 决定（实际是定向发给房主，
    /// 由房主定序后广播回来）。这就是三层职责里的 Client 层。
    /// </para>
    /// <para>由 <see cref="EasyMulti.Client.Connect"/> 开出来；事件全部在 <see cref="EasyMulti.Poll"/> 里、同一个线程上回调。</para>
    /// </summary>
    public sealed class EasyMultiClient
    {
        private readonly RelaySession _session;
        private readonly Action _close;
        private readonly PayloadRouter _router = new PayloadRouter();

        internal EasyMultiClient(RelaySession session, Action close)
        {
            _session = session;
            _close = close;
            _session.RoomListChanged  += rooms      => RoomsChanged?.Invoke(ToRooms(rooms));
            _session.RoomJoined       += code       => Joined?.Invoke(code);
            _session.LeftRoom         += ()         => Left?.Invoke();
            _session.GameDataReceived += (from, data) => _router.Dispatch(from, data); // 只可能来自房主
            _session.HostDropped      += ()         => HostDropped?.Invoke();
            _session.HostBack         += ()         => HostBack?.Invoke();
            _session.Rejected         += reason     => Rejected?.Invoke(reason);
            _session.Disconnected     += reason     => Disconnected?.Invoke(reason);
        }

        // ── 状态：你随时可以问它 ─────────────────────────────────────────

        /// <summary>你的 playerId —— 身份标识，不是显示名（显示名是游戏层自己的事）。</summary>
        public string Id => _session.Config.PlayerId;

        /// <summary>
        /// 中继已经认下你了吗。<b>连上中继就等于在大厅</b>，所以这一个值就说明了
        /// 「我在不在线、在不在大厅」——不需要一个频道来播报它。
        /// <para>进没进房间看 <see cref="Joined"/> / <see cref="Left"/>，那是真正的状态转移。</para>
        /// </summary>
        public bool Connected => _session.IsRegistered;

        /// <summary>当前房间的房码；不在房间里时是 null。</summary>
        public string? RoomCode => _session.GameCode;

        // ── 动作：你调用它 ───────────────────────────────────────────────
        //
        // 前三个都不必等连上：还没注册完就先攒着，注册成功后按调用顺序补发。

        /// <summary>要一份房间列表。结果走 <see cref="RoomsChanged"/> 回来。中继绝不主动推。</summary>
        public void RefreshRooms() => _session.RefreshRooms();

        /// <summary>申请进这个房间。成功走 <see cref="Joined"/> 回来，被拒走 <see cref="Rejected"/>。</summary>
        public void Join(string roomCode) => _session.JoinRoom(roomCode);

        /// <summary>退出房间回大厅。走 <see cref="Left"/> 回来。</summary>
        public void Leave() => _session.LeaveRoom();

        /// <summary>
        /// <b>玩家的游戏逻辑只需要这个</b>：把一个消息对象交出去。
        /// <b>T 就是消息通道</b>：对端只有 <c>Receive&lt;T&gt;</c> 了同一个 T 才会收到。
        /// 默认壳走 <see cref="EasyMulti.Codec"/>（推荐 MemoryPack），管道零膨胀直通。
        /// 发到哪儿去不是玩家该操心的事。没进房间时是空操作。
        /// </summary>
        public void Send<T>(T value)
        {
            string host = _session.HostId ?? "";
            if (host.Length == 0) return;
            _session.SendGameData(PayloadRouter.EncodeMessage(value), to: host);
        }

        /// <summary>
        /// 订一条类型通道：房主发来的 <typeparamref name="T"/> 消息从这里进。
        /// 没订阅的类型静默丢弃；同一 T 多次订阅＝叠加。
        /// </summary>
        public void Receive<T>(Action<T> handler) => _router.Register<T>((_, value) => handler(value));

        /// <summary>下线（回主菜单/切账号）。想重新上线就再 <see cref="EasyMulti.Client.Connect"/> 一个。</summary>
        public void Disconnect() => _close();

        // ── 频道：你订阅它，消息推给你 ─────────────────────────────────────

        /// <summary>房间列表来了。<b>只会在你调过 <see cref="RefreshRooms"/> 之后触发。</b></summary>
        public event Action<IReadOnlyList<Room>>? RoomsChanged;

        /// <summary>进房间了。<b>游戏逻辑从这里开始。</b>参数是房码。</summary>
        public event Action<string>? Joined;

        /// <summary>退出房间了，回到大厅。</summary>
        public event Action? Left;

        /// <summary>房主掉线了（对局暂停，等他回来或散伙）。你还在房间里，座位没动。</summary>
        public event Action? HostDropped;

        /// <summary>房主回来了，对局继续。</summary>
        public event Action? HostBack;

        /// <summary>某个请求被拒了：名字被占、房间满了、房间已开局之类。连接还在，可以重试。</summary>
        public event Action<string>? Rejected;

        /// <summary>与中继的连接断了。参数是原因。</summary>
        public event Action<string>? Disconnected;

        private static Room[] ToRooms(IReadOnlyList<RoomInfo> rooms)
        {
            var result = new Room[rooms.Count];
            for (int i = 0; i < rooms.Count; i++)
            {
                RoomInfo r = rooms[i];
                result[i] = new Room(r.Code, r.Name, r.PlayerCount, r.MaxPlayers, r.InGame);
            }

            return result;
        }
    }
}
