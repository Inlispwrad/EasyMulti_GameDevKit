#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using EasyMultiNet.Protocol;

namespace EasyMultiNet
{
    public enum SessionState
    {
        Disconnected,
        Connecting,

        /// <summary>
        /// 传输通了，等中继确认身份。凭证已经随连接请求发出去了（不需要再发什么），
        /// 只是还没收到 REGISTER_SUCCESS —— 走到这一步说明凭证本身已经过了门口。
        /// </summary>
        Unregistered,
        Lobby,
        InRoom,
        InGame,
    }

    /// <summary>
    /// 低层协议会话：connect → REGISTER → create/join a room → START_GAME →
    /// exchange GAME_DATA。它 1:1 映射线协议，玩家和房主共用 —— 房主就是调了
    /// <see cref="CreateRoom"/> 的那个会话（一条<b>专门的</b>连接，不进玩家名单）。
    /// <para>
    /// 游戏代码通常不直接用它，而是用 <see cref="EasyMulti"/> 门面开出来的
    /// <see cref="EasyMultiClient"/> / <see cref="EasyMultiHost"/> —— 那两个类才把「三层职责」
    /// 分开。这个类留给测试、工具和确实需要整个协议面的高级用法。
    /// </para>
    /// <para>
    /// <b>Transport-agnostic and game-agnostic:</b> it depends only on
    /// <see cref="IClientTransport"/> and treats <c>GAME_DATA.data</c> as an opaque string.
    /// Single-threaded — <see cref="Poll"/> runs on the caller's loop and all events fire
    /// inside it.
    /// </para>
    /// <para>
    /// 房间指令（<see cref="RefreshRooms"/> 等）<b>不必等连上</b>：注册完成前先攒着，
    /// 注册成功后按调用顺序补发。中继在注册前会把这类消息<b>静默丢弃</b>，没有这层
    /// 缓冲，调用方就得自己记住「要先等注册」，忘了就是查不出的卡死。
    /// </para>
    /// </summary>
    public sealed class RelaySession : IDisposable
    {
        private readonly IClientTransport _transport;
        private readonly List<string> _roomPlayers = new List<string>();
        private readonly List<RoomInfo> _rooms = new List<RoomInfo>();
        private readonly List<Action> _pending = new List<Action>();
        private bool _open;

        public RelaySession(SessionConfig config, IClientTransport transport)
        {
            Config = config;
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _transport.Opened += OnOpened;
            _transport.Closed += OnClosed;
            _transport.Received += OnReceived;
            _transport.ReceivedBinary += OnReceivedBinary;
        }

        /// <summary>Convenience: WebSocket-backed session.</summary>
        public static RelaySession CreateWebSocket(SessionConfig config) =>
            new RelaySession(config, new WebSocketClientTransport());

        /// <summary>Convenience: UDP-backed session.</summary>
        public static RelaySession CreateUdp(SessionConfig config) =>
            new RelaySession(config, new UdpClientTransport());

        public SessionConfig Config { get; }

        public SessionState State { get; private set; } = SessionState.Disconnected;

        /// <summary>中继已经认下这条连接（REGISTER 成功且未断开）。</summary>
        public bool IsRegistered { get; private set; }

        /// <summary>Current room code; null when not in a room.</summary>
        public string? GameCode { get; private set; }

        /// <summary>当前房间的玩家名单（host 不是玩家，不在其中）。不在房间时为空。</summary>
        public IReadOnlyList<string> RoomPlayers => _roomPlayers;

        /// <summary>Current lobby snapshot for this game.</summary>
        public IReadOnlyList<RoomInfo> Rooms => _rooms;

        /// <summary>Rooms still joinable (not yet started). The lobby's "筛选进行中的房间" helper.</summary>
        public IReadOnlyList<RoomInfo> JoinableRooms => _rooms.Where(r => !r.InGame).ToList();

        /// <summary>当前房间的房主连接名（进房时随 JOIN_SUCCESS 给）。不在房间时为 null。</summary>
        public string? HostId { get; private set; }

        public bool IsHost => GameCode != null && HostId == Config.PlayerId;

        // ── Events (all fired inside Poll) ────────────────────────────────────

        public event Action? Registered;
        public event Action<IReadOnlyList<RoomInfo>>? RoomListChanged;
        public event Action<string>? RoomCreated;
        public event Action<string>? RoomJoined;
        public event Action<IReadOnlyList<string>>? RoomPlayersChanged;

        /// <summary>某成员掉线（座位仍保留）。参数是 playerId。</summary>
        public event Action<string>? PlayerDisconnected;

        /// <summary>某成员重连坐回（座位重新接上）。参数是 playerId。Host 可借此给他补发局面。</summary>
        public event Action<string>? PlayerReconnected;

        /// <summary>房主掉线了（座位保留，对局等他回来）。</summary>
        public event Action? HostDropped;

        /// <summary>掉线的房主重连坐回来了。</summary>
        public event Action? HostBack;

        /// <summary>房主换了（自动转交：首个在线玩家被提出名单立为新 host）。参数是新 host 名；等于自己名字时表示「我是新房主」。</summary>
        public event Action<string>? HostChanged;

        public event Action? GameStarted;
        public event Action? LeftRoom;

        /// <summary>收到对局数据 (发件人 id, 原始字节)。字节怎么解是你们两端自己的协议。</summary>
        public event Action<string, byte[]>? GameDataReceived;

        /// <summary>某个请求被中继拒绝（注册失败、加入失败）。连接还在，可以换个方式重试。</summary>
        public event Action<string>? Rejected;

        /// <summary>与中继的连接断了（连不上、被断开）。参数是人话原因。</summary>
        public event Action<string>? Disconnected;

        // ── Outbound ──────────────────────────────────────────────────────────

        /// <summary>
        /// Connect to the relay. 凭证随连接请求一起走 —— 中继验完才算连上，
        /// 验不过会走 <see cref="Rejected"/>（不是 <see cref="Disconnected"/>：从没连上过）。
        /// </summary>
        public void Connect(string host, int port)
        {
            if (State != SessionState.Disconnected) throw new InvalidOperationException("已经连过了");
            State = SessionState.Connecting;
            _transport.Connect(host, port, new RegisterRequest(Config.Token, Config.GameId, Config.PlayerId));
        }

        /// <summary>Drive the transport and fire events. Call every 10–20 ms.</summary>
        public void Poll() => _transport.Poll();

        public void RefreshRooms() => Ready(() => Send(new ListRoomsRequest()));

        /// <param name="maxPlayers">玩家容量。房主是专门的连接，不占这个数。</param>
        /// <param name="autoHostTransfer">房主掉线是否把首个在线玩家提拔为新 host（玩家客户端也带 host 逻辑时才有意义）。</param>
        /// <param name="dedicated">独立部署的 host（专服）：中继会拒绝与 host 裸名同名的玩家加入。</param>
        public void CreateRoom(string? roomName = null, int? maxPlayers = null, bool? autoHostTransfer = null, bool? dedicated = null) =>
            Ready(() => Send(new CreateRoomRequest(roomName, maxPlayers, autoHostTransfer, dedicated)));

        public void JoinRoom(string gameCode) => Ready(() => Send(new JoinRoomRequest(gameCode)));

        public void LeaveRoom() => Ready(() => Send(new LeaveRoomRequest()));

        /// <summary>Remove a member from the room. Host only.</summary>
        public void Kick(string playerId) => Ready(() => Send(new KickRequest(playerId)));

        /// <summary>Mark the room as in-game（封盘：大厅里显示进行中，新人不能再进）. Host only.</summary>
        public void StartGame() => Ready(() => Send(new StartGameRequest()));

        /// <summary>
        /// 发一条对局数据（原始字节，中继盲转零膨胀）。不回显给发件人。
        /// </summary>
        /// <param name="to">收件人 id；缺省＝广播给房间内其他所有连接。</param>
        public void SendGameData(byte[] payload, string? to = null, DeliveryMode mode = DeliveryMode.Reliable)
        {
            if (!_open) throw new InvalidOperationException("还没连上中继");
            _transport.SendBinary(GameDataFraming.Encode(to ?? "", payload), mode);
        }

        public void Dispose()
        {
            _open = false;
            IsRegistered = false;
            _pending.Clear();
            _transport.Dispose();
            State = SessionState.Disconnected;
        }

        // ── Inbound ───────────────────────────────────────────────────────────

        private void OnOpened()
        {
            // 传输通了 ≠ 中继收下了：凭证已经随连接请求发出，等中继的 REGISTER_SUCCESS。
            _open = true;
            State = SessionState.Unregistered;
        }

        private void OnClosed(string reason)
        {
            // 从没连上过就断了 ＝ 被拒（凭证不对 / 名字被占），不是掉线。
            bool refused = !IsRegistered;

            _open = false;
            IsRegistered = false;
            _pending.Clear(); // 连都断了，补发没有意义
            State = SessionState.Disconnected;
            _roomPlayers.Clear();
            _rooms.Clear();
            GameCode = null;
            HostId = null;

            if (refused) Rejected?.Invoke("连接被拒：" + reason);
            else Disconnected?.Invoke(reason);
        }

        private void OnReceived(string json, DeliveryMode mode)
        {
            if (!RelayCodec.TryReadType(json, out string type)) return;

            switch (type)
            {
                case RelayMessageType.RegisterSuccess:
                    State = SessionState.Lobby;
                    IsRegistered = true;
                    // 先补发攒下的指令再播报 Registered —— 订阅者在回调里再发指令，
                    // 顺序也一定排在补发之后。
                    FlushPending();
                    Registered?.Invoke();
                    break;

                case RelayMessageType.RegisterFailed:
                    if (RelayCodec.TryDeserialize(json, out RegisterFailedMessage regFail))
                    {
                        Rejected?.Invoke("注册失败：" + regFail.Reason);
                    }

                    break;

                case RelayMessageType.RoomList:
                    if (RelayCodec.TryDeserialize(json, out RoomListMessage list))
                    {
                        SetRooms(list.Rooms);
                    }

                    break;

                case RelayMessageType.RoomCreated:
                    if (RelayCodec.TryDeserialize(json, out RoomCreatedMessage created))
                    {
                        GameCode = created.GameCode;
                        State = SessionState.InRoom;
                        HostId = Config.PlayerId; // 开房的就是 host；玩家名单此刻为空
                        RoomCreated?.Invoke(created.GameCode);
                    }

                    break;

                case RelayMessageType.JoinSuccess:
                    if (RelayCodec.TryDeserialize(json, out JoinSuccessMessage joined))
                    {
                        GameCode = joined.GameCode;
                        State = SessionState.InRoom;
                        HostId = joined.HostId;
                        SetRoomPlayers(joined.Players);
                        RoomJoined?.Invoke(joined.GameCode);
                    }

                    break;

                case RelayMessageType.JoinFailed:
                    if (RelayCodec.TryDeserialize(json, out JoinFailedMessage joinFail))
                    {
                        Rejected?.Invoke("加入房间失败：" + joinFail.Reason);
                    }

                    break;

                case RelayMessageType.PlayerJoined:
                    if (RelayCodec.TryDeserialize(json, out PlayerJoinedMessage pj))
                    {
                        SetRoomPlayers(pj.Players);
                    }

                    break;

                case RelayMessageType.PlayerLeft:
                    if (RelayCodec.TryDeserialize(json, out PlayerLeftMessage pl))
                    {
                        SetRoomPlayers(pl.Players);
                    }

                    break;

                case RelayMessageType.PlayerDisconnected:
                    // 成员掉线但座位保留：名单不变（他还在 players 里）。
                    if (RelayCodec.TryDeserialize(json, out PlayerDisconnectedMessage pd))
                    {
                        SetRoomPlayers(pd.Players);
                        PlayerDisconnected?.Invoke(pd.PlayerId);
                    }

                    break;

                case RelayMessageType.PlayerReconnected:
                    if (RelayCodec.TryDeserialize(json, out PlayerReconnectedMessage pr))
                    {
                        SetRoomPlayers(pr.Players);
                        PlayerReconnected?.Invoke(pr.PlayerId);
                    }

                    break;

                case RelayMessageType.HostDropped:
                    HostDropped?.Invoke();
                    break;

                case RelayMessageType.HostBack:
                    HostBack?.Invoke();
                    break;

                case RelayMessageType.HostChanged:
                    if (RelayCodec.TryDeserialize(json, out HostChangedMessage hc))
                    {
                        HostId = hc.HostId;
                        SetRoomPlayers(hc.Players);
                        HostChanged?.Invoke(hc.HostId);
                    }

                    break;

                case RelayMessageType.GameStarted:
                    State = SessionState.InGame;
                    GameStarted?.Invoke();
                    break;

                case RelayMessageType.LeaveSuccess:
                    State = SessionState.Lobby;
                    GameCode = null;
                    HostId = null;
                    _roomPlayers.Clear();
                    LeftRoom?.Invoke();
                    break;

            }
        }

        private void OnReceivedBinary(byte[] frame, DeliveryMode mode)
        {
            if (GameDataFraming.TryDecode(frame, out string from, out byte[] payload))
            {
                GameDataReceived?.Invoke(from, payload);
            }
        }

        private void SetRoomPlayers(IReadOnlyList<string> players)
        {
            if (_roomPlayers.SequenceEqual(players)) return;
            _roomPlayers.Clear();
            _roomPlayers.AddRange(players);
            RoomPlayersChanged?.Invoke(_roomPlayers);
        }

        private void SetRooms(IReadOnlyList<RoomInfo> rooms)
        {
            _rooms.Clear();
            _rooms.AddRange(rooms);
            RoomListChanged?.Invoke(_rooms);
        }

        /// <summary>跑一条房间指令：已注册就直接发，否则攒到注册成功后按序补发。</summary>
        private void Ready(Action send)
        {
            if (IsRegistered) send();
            else _pending.Add(send);
        }

        private void FlushPending()
        {
            foreach (Action send in _pending) send();
            _pending.Clear();
        }

        private void Send(object message, DeliveryMode mode = DeliveryMode.Reliable)
        {
            if (!_open) throw new InvalidOperationException("还没连上中继");
            _transport.Send(RelayCodec.Serialize(message), mode);
        }
    }
}
