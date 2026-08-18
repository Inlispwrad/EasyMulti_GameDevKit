#nullable enable

using System.Collections.Concurrent;
using EasyMultiNet.Protocol;
using EasyMultiNet.Relay.Transport;

namespace EasyMultiNet.Relay;

/// <summary>
/// The relay core. Owns the game/room state and the single-threaded event loop.
/// <para>
/// All I/O (WebSocket pumps, UDP receive loop) funnels through <see cref="_events"/>;
/// the main loop dequeues and dispatches, so every handler below runs on one thread and
/// needs no locks. The relay only forwards <c>GAME_DATA.data</c> — it never parses it.
/// </para>
/// <para>
/// Reconnection: a member whose connection drops keeps their <b>seat</b> (name) in the room
/// indefinitely. They can re-register with the same playerId and JOIN_ROOM(code) to
/// re-attach — even if the game already started. Removing a seat is host-driven (KICK), and
/// a room with no live members is destroyed. No time-based logic lives here.
/// </para>
/// </summary>
public sealed class RelayServer
{
    private readonly RelayConfig _config;
    private readonly List<IRelayTransport> _transports = new();
    private readonly ConcurrentQueue<RelayEvent> _events = new();

    private readonly Dictionary<IRelayConnection, PeerState> _peers = new();
    private readonly Dictionary<string, Dictionary<string, Room>> _games = new();

    // Anti-crawler throttle: bad-token attempts per source IP.
    private readonly ConcurrentDictionary<string, (int Count, long WindowStartMs)> _badAuth = new();

    private readonly Random _rng = new();
    private volatile bool _running;
    private volatile Counts _counts = new(0, 0, 0, 0);

    public RelayServer(RelayConfig config) => _config = config;

    public void Start()
    {
        if (_config.WebSocketEnabled)
        {
            _transports.Add(new WebSocketTransport(_config.WebSocketPort));
        }

        if (_config.UdpEnabled)
        {
            _transports.Add(new UdpTransport(_config.UdpPort, new UdpPeerOptions
            {
                IdleTimeoutMs = _config.IdleTimeoutMs,
            }));
        }

        foreach (IRelayTransport transport in _transports)
        {
            transport.Start(Enqueue, Authenticate);
        }
    }

    /// <summary>
    /// Run the relay until <see cref="Stop"/> is called. A 1 ms poll keeps forwarding
    /// latency low and drives the UDP transports' <c>Tick()</c>. No time-based game logic
    /// lives here — seat removal is host-driven (LEAVE_ROOM / KICK).
    /// </summary>
    public void Run()
    {
        Start();
        _running = true;
        while (_running)
        {
            var dispatched = false;
            while (_events.TryDequeue(out RelayEvent e))
            {
                Dispatch(e);
                dispatched = true;
            }

            if (dispatched) PublishCounts();

            foreach (IRelayTransport transport in _transports)
            {
                transport.Tick();
            }

            Thread.Sleep(1);
        }
    }

    public void Stop()
    {
        _running = false;
        foreach (IRelayTransport transport in _transports)
        {
            transport.Stop();
        }
    }

    private void Enqueue(RelayEvent e) => _events.Enqueue(e);

    /// <summary>
    /// 连接请求的门口检查 —— 在传输层创建任何对象**之前**跑，验不过的连接压根不会存在。
    /// <para>
    /// 跑在传输的 I/O 线程上，所以这里只许看配置（token、gameId/playerId 的格式）。
    /// 需要看中继状态的判断（名字有没有被占、连接数满没满）留给核心线程上的
    /// <see cref="OnConnected"/> —— 那时对方已经是持有效 token 的合法客户端了。
    /// </para>
    /// </summary>
    /// <returns>null＝放行；非 null＝拒绝的理由。</returns>
    private string? Authenticate(RegisterRequest credentials, string address)
    {
        if (!IsValidToken(credentials.Token))
        {
            Log($"连接被拒：token 无效（{address}）");
            Throttle(address);
            return "bad_token";
        }

        if (!IsValidGameId(credentials.GameId.Trim())) return "bad_game_id";

        string playerId = credentials.PlayerId.Trim();
        if (playerId.Length == 0 || playerId.Length > 64) return "bad_request";

        return null;
    }

    // ── Event dispatch ────────────────────────────────────────────────────────

    private void Dispatch(RelayEvent e)
    {
        switch (e.Kind)
        {
            case RelayEventKind.Connected:
                OnConnected(e.Connection);
                break;

            case RelayEventKind.Message:
                if (!_peers.TryGetValue(e.Connection, out PeerState? state))
                {
                    break;
                }

                if (e.Binary != null)
                {
                    OnGameData(e.Connection, state, e.Binary, e.Mode);
                }
                else if (e.Text != null)
                {
                    OnMessage(e.Connection, state, e.Text, e.Mode);
                }

                break;

            case RelayEventKind.Disconnected:
                OnDisconnected(e.Connection, e.Reason);
                break;
        }
    }

    /// <summary>
    /// 一条已经验过凭证的连接进来了。到这里身份已经成立（凭证在传输层的门口就验完了），
    /// 剩下的是只有核心线程才看得到的两件事：连接数满没满、名字被没被占。
    /// </summary>
    private void OnConnected(IRelayConnection connection)
    {
        if (_peers.Count >= _config.MaxConnections)
        {
            Log($"拒绝连接 {connection.Address}：达到最大连接数 {_config.MaxConnections}");
            Send(connection, new RegisterFailedMessage("server_full"));
            connection.Close("server_full");
            return;
        }

        string playerId = connection.Credentials.PlayerId.Trim();
        string gameId = connection.Credentials.GameId.Trim();

        // 名字撞车不是鉴权失败：对方已经用有效 token 证明了自己是合法客户端，给它一个槽位
        // 跟给任何正常玩家一样安全。所以这里走正常的消息通道告诉它，浏览器能收到。
        if (_peers.Values.Any(s => s.PlayerId == playerId && string.Equals(s.GameId, gameId, StringComparison.Ordinal)))
        {
            Send(connection, new RegisterFailedMessage("name_taken"));
            connection.Close("name_taken"); // UDP 下这条理由还会跟着 Bye 走一遍，丢包也不会失联
            return;
        }

        _peers[connection] = new PeerState { PlayerId = playerId, GameId = gameId };
        Log($"Player connected: {playerId} @ {gameId} ({connection.Address}) [{connection.TransportName}]");
        Send(connection, new RegisterSuccessMessage());
        // 不主动塞房间列表：客户端要就自己发 LIST_ROOMS。见下面 Room list 一节。
    }

    private void OnDisconnected(IRelayConnection connection, string reason)
    {
        if (!_peers.TryGetValue(connection, out PeerState? state))
        {
            return;
        }

        string who = state.PlayerId ?? "（未注册）";
        if (state.Location == Loc.InRoom)
        {
            ReserveSeat(connection, state); // 掉线保留座位，等重连
        }

        _peers.Remove(connection);
        Log($"Peer disconnected: {connection.Address} {who} ({connection.TransportName}) — {reason}");
    }

    private void OnMessage(IRelayConnection connection, PeerState state, string json, DeliveryMode mode)
    {
        if (!RelayCodec.TryReadType(json, out string type))
        {
            return;
        }

        switch (type)
        {
            case RelayMessageType.ListRooms:  SendRoomList(connection, state.GameId!); break;
            case RelayMessageType.CreateRoom: OnCreateRoom(connection, state, json); break;
            case RelayMessageType.JoinRoom:   OnJoinRoom(connection, state, json);   break;
            case RelayMessageType.LeaveRoom:  LeaveRoom(connection, state);         break;
            case RelayMessageType.Kick:       OnKick(connection, state, json);      break;
            case RelayMessageType.StartGame:  OnStartGame(connection, state);       break;
        }
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private void OnCreateRoom(IRelayConnection connection, PeerState state, string json)
    {
        if (state.Location != Loc.Lobby) return;
        if (!RelayCodec.TryDeserialize<CreateRoomRequest>(json, out CreateRoomRequest req)) return;

        string roomName = string.IsNullOrWhiteSpace(req.RoomName) ? "Room" : req.RoomName!;
        int maxPlayers = req.MaxPlayers ?? 4;
        if (maxPlayers < 1) maxPlayers = 1;      // 玩家容量，host 不占数
        if (maxPlayers > 1024) maxPlayers = 1024;

        string gameId = state.GameId!;
        string code = GenerateCode(gameId);
        var room = new Room
        {
            Code = code,
            Name = roomName,
            MaxPlayers = maxPlayers,
            AutoHostTransfer = req.AutoHostTransfer ?? false,
            Dedicated = req.Dedicated ?? false,
            Host = new RoomSeat { Name = state.PlayerId!, Conn = connection },
        };

        Rooms(gameId)[code] = room;
        state.Location = Loc.InRoom;
        state.GameCode = code;

        Log($"Room created: {code} '{roomName}' by {state.PlayerId} @ {gameId}");
        Send(connection, new RoomCreatedMessage(code));
        InvalidateRoomList(gameId);
    }

    private void OnJoinRoom(IRelayConnection connection, PeerState state, string json)
    {
        if (state.Location != Loc.Lobby) return;
        if (!RelayCodec.TryDeserialize<JoinRoomRequest>(json, out JoinRoomRequest req)) return;

        string gameId = state.GameId!;
        string code = req.GameCode;
        string playerId = state.PlayerId!;
        if (string.IsNullOrEmpty(code)) return;

        if (!Rooms(gameId).TryGetValue(code, out Room? room))
        {
            Send(connection, new JoinFailedMessage("room_not_found"));
            return;
        }

        // 房主重连：host 席位同名且空着 → 坐回，玩家收 HOST_BACK。
        if (room.Host.Name == playerId && room.Host.Conn == null)
        {
            room.Host.Conn = connection;
            state.Location = Loc.InRoom;
            state.GameCode = code;

            Send(connection, new JoinSuccessMessage(code, room.Host.Name, Names(room)));
            if (room.InGame) Send(connection, new GameStartedMessage());
            SendToRoom(room, new HostBackMessage(), except: connection);
            Log($"Host reconnected to room {code}: {playerId} @ {gameId}");
            InvalidateRoomList(gameId);
            return;
        }

        // 玩家重连：有同名保留座位 → 直接坐回（无论房间是否开局）。名单即准入，无时限。
        RoomSeat? seat = room.Players.FirstOrDefault(p => p.Name == playerId && p.Conn == null);
        if (seat != null)
        {
            seat.Conn = connection;
            state.Location = Loc.InRoom;
            state.GameCode = code;

            Send(connection, new JoinSuccessMessage(code, room.Host.Name, Names(room)));
            if (room.InGame) Send(connection, new GameStartedMessage());
            SendToRoom(room, new PlayerReconnectedMessage(playerId, Names(room)), except: connection);
            Log($"Peer reconnected to room {code}: {playerId} @ {gameId}");
            return;
        }

        // 新加入。
        if (room.InGame)
        {
            Send(connection, new JoinFailedMessage("game_already_started"));
            return;
        }

        if (room.Players.Count >= room.MaxPlayers)
        {
            Send(connection, new JoinFailedMessage("room_full"));
            return;
        }

        if (room.Players.Any(p => p.Name == playerId))
        {
            Send(connection, new JoinFailedMessage("name_taken"));
            return;
        }

        // 独立 host（专服）没有「玩家人格」：与 host 裸名同名的玩家不放进来。
        // 普通房不拦 —— 「自己开房自己玩」的房主本人就叫这个名字。
        if (room.Dedicated && room.Host.Name == playerId + RelayNaming.HostSuffix)
        {
            Send(connection, new JoinFailedMessage("name_reserved"));
            return;
        }

        room.Players.Add(new RoomSeat { Name = playerId, Conn = connection });
        state.Location = Loc.InRoom;
        state.GameCode = code;

        Send(connection, new JoinSuccessMessage(code, room.Host.Name, Names(room)));
        SendToRoom(room, new PlayerJoinedMessage(playerId, Names(room)), except: connection);
        Log($"Peer joined room {code}: {playerId} @ {gameId}");
        InvalidateRoomList(gameId);
    }

    private void LeaveRoom(IRelayConnection connection, PeerState state)
    {
        if (state.Location != Loc.InRoom || state.GameCode is null) return;
        if (!Rooms(state.GameId!).TryGetValue(state.GameCode, out Room? room)) return;

        // 房主主动离开 = 解散：所有在线玩家送回大厅，房间销毁。
        // （意外断线走 ReserveSeat：座位保留、玩家收 HOST_DROPPED，是另一条路。）
        if (room.Host.Conn == connection)
        {
            state.Location = Loc.Lobby;
            state.GameCode = null;
            Send(connection, new LeaveSuccessMessage());
            DisbandRoom(room, state.GameId!);
            return;
        }

        string leavingName = state.PlayerId ?? "Unknown";
        room.Players.RemoveAll(p => p.Conn == connection);
        state.Location = Loc.Lobby;
        state.GameCode = null;

        if (!DestroyIfNoLiveMembers(room, state.GameId!))
        {
            SendToRoom(room, new PlayerLeftMessage(leavingName, Names(room)));
        }

        Send(connection, new LeaveSuccessMessage());
        InvalidateRoomList(state.GameId!);
    }

    /// <summary>解散房间：在线玩家全部送回大厅（各收一份 LeaveSuccess），房间移除。</summary>
    private void DisbandRoom(Room room, string gameId)
    {
        foreach (RoomSeat player in room.Players)
        {
            if (player.Conn == null || !_peers.TryGetValue(player.Conn, out PeerState? playerState)) continue;

            playerState.Location = Loc.Lobby;
            playerState.GameCode = null;
            Send(player.Conn, new LeaveSuccessMessage());
        }

        EraseRoom(room, gameId);
        Log($"Room disbanded by host: {room.Code}");
        InvalidateRoomList(gameId);
    }

    private void OnStartGame(IRelayConnection connection, PeerState state)
    {
        if (state.Location != Loc.InRoom || state.GameCode is null) return;
        if (!Rooms(state.GameId!).TryGetValue(state.GameCode, out Room? room)) return;
        if (room.Host.Conn != connection) return; // 只有房主能封盘

        room.InGame = true;
        Log($"Game started in room {state.GameCode}");
        SendToRoom(room, new GameStartedMessage());
        InvalidateRoomList(state.GameId!);
    }

    /// <summary>
    /// 对局数据（二进制帧 [路由头+payload]）：中继只读路由头、换头转发，
    /// payload 一个字节不解析 —— MemoryPack 等任意二进制编码零膨胀直通。
    /// </summary>
    private void OnGameData(IRelayConnection connection, PeerState state, byte[] frame, DeliveryMode mode)
    {
        if (state.Location != Loc.InRoom || state.GameCode is null) return;
        if (!Rooms(state.GameId!).TryGetValue(state.GameCode, out Room? room)) return;
        if (!IsMember(room, connection)) return; // 硬约束：只有房间成员能发对局数据
        if (!GameDataFraming.TryDecode(frame, out string to, out byte[] payload)) return;

        // 换头：去掉收件人，换成发件人；payload 原样搬运，只构造一次。
        byte[] forward = GameDataFraming.Encode(state.PlayerId ?? "", payload);

        if (to.Length > 0)
        {
            // 收件人可以是玩家，也可以是 host（玩家把输入交给房主走的就是这条路）。
            RoomSeat? target = room.Host.Name == to
                ? room.Host
                : room.Players.FirstOrDefault(pl => pl.Name == to);
            if (target?.Conn != null && target.Conn != connection)
            {
                target.Conn.SendBinary(forward, mode);
            }

            return;
        }

        SendToRoomBinary(room, forward, except: connection, mode: mode);
    }

    /// <summary>把一帧对局数据发给房间里所有在线连接（host + 玩家），帧只构造一次。</summary>
    private static void SendToRoomBinary(Room room, byte[] frame, IRelayConnection? except, DeliveryMode mode)
    {
        if (room.Host.Conn != null && room.Host.Conn != except) room.Host.Conn.SendBinary(frame, mode);
        foreach (RoomSeat pl in room.Players)
        {
            if (pl.Conn != null && pl.Conn != except) pl.Conn.SendBinary(frame, mode);
        }
    }

    // ── Reconnection & removal ───────────────────────────────────────────────

    /// <summary>把断开连接的成员标记为「保留座位」，等它同名重连。无时限，移除由 Host 决定。</summary>
    private void ReserveSeat(IRelayConnection connection, PeerState state)
    {
        if (state.GameCode is null) return;
        if (!Rooms(state.GameId!).TryGetValue(state.GameCode, out Room? room)) return;

        // 座位状态要变（host 掉线时房间列表里的 hostId 也可能跟着变），
        // 缓存必须失效 —— 见 RoomsPayload 上的说明。
        InvalidateRoomList(state.GameId!);

        // 房主掉线。
        if (room.Host.Conn == connection)
        {
            room.Host.Conn = null;
            Log($"Host seat reserved for reconnect: {room.Host.Name} in room {room.Code}");

            // 开房时声明了自动转交：把首个在线玩家从名单里提出来，立为新 host。
            // （只对「玩家客户端也带着 host 逻辑」的形态有意义。）
            if (room.AutoHostTransfer)
            {
                int next = room.Players.FindIndex(p => p.Conn != null);
                if (next >= 0)
                {
                    RoomSeat promoted = room.Players[next];
                    room.Players.RemoveAt(next);
                    room.Host = promoted;
                    Log($"Host transferred: → {promoted.Name} in room {room.Code}");
                    SendToRoom(room, new HostChangedMessage(promoted.Name, Names(room)));
                    return;
                }
            }

            SendToRoom(room, new HostDroppedMessage());
            DestroyIfNoLiveMembers(room, state.GameId!);
            return;
        }

        // 玩家掉线。
        RoomSeat? seat = room.Players.FirstOrDefault(p => p.Conn == connection);
        if (seat == null) return;

        seat.Conn = null;
        Log($"Seat reserved for reconnect: {seat.Name} in room {room.Code}");

        SendToRoom(room, new PlayerDisconnectedMessage(seat.Name, Names(room)));

        // 房间里没有在线成员了 → 销毁（僵尸房间清理）。
        DestroyIfNoLiveMembers(room, state.GameId!);
    }

    /// <summary>房主踢人：把指定玩家从名单移除（在线则送回大厅），通知其余人。host 不在名单里，天然踢不到。</summary>
    private void OnKick(IRelayConnection connection, PeerState state, string json)
    {
        if (state.Location != Loc.InRoom || state.GameCode is null) return;
        if (!Rooms(state.GameId!).TryGetValue(state.GameCode, out Room? room)) return;
        if (room.Host.Conn != connection) return; // 只有房主能踢
        if (!RelayCodec.TryDeserialize<KickRequest>(json, out KickRequest req)) return;

        RoomSeat? target = room.Players.FirstOrDefault(p => p.Name == req.PlayerId);
        if (target == null) return;

        RemoveSeat(room, target, state.GameId!);
    }

    /// <summary>把某个玩家座位从房间移除：在线者送回大厅，其余人收 PLAYER_LEFT。</summary>
    private void RemoveSeat(Room room, RoomSeat target, string gameId)
    {
        room.Players.Remove(target);
        Log($"Seat removed: {target.Name} in room {room.Code}");

        // 目标在线 → 送回大厅。
        if (target.Conn != null && _peers.TryGetValue(target.Conn, out PeerState? targetState))
        {
            targetState.Location = Loc.Lobby;
            targetState.GameCode = null;
            Send(target.Conn, new LeaveSuccessMessage());
        }

        if (!DestroyIfNoLiveMembers(room, gameId))
        {
            SendToRoom(room, new PlayerLeftMessage(target.Name, Names(room)));
        }
        else
        {
            return; // 房间已销毁，DestroyIfNoLiveMembers 已通知大厅
        }

        InvalidateRoomList(gameId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 擦掉一间房，顺手把**全表**的空 gameId 条目一起收掉（连它的房间列表缓存）。
    /// <para>
    /// 清理为什么挂在这里：<see cref="Rooms"/> 是 get-or-create，一次「拿不存在的房码去
    /// JOIN」就会给一个客户端自填的 gameId 建出空字典，而那条路径永远走不到销毁房间这里来。
    /// gameId 上限 64 字符、基数无限，所以不能只清「刚擦掉那一间房的 gameId」。挂在「有房间
    /// 被擦除」这个必然反复发生的时刻上，不用另开定时器。
    /// </para>
    /// <para>
    /// 代价是每次擦房 O(gameId 数)；换来的是空条目活不过下一次房间擦除。
    /// 边界：一台完全没有房间生灭的中继不会触发清扫 —— 但那种中继上也没人在玩。
    /// </para>
    /// </summary>
    private void EraseRoom(Room room, string gameId)
    {
        Rooms(gameId).Remove(room.Code);

        List<string>? empty = null;
        foreach (KeyValuePair<string, Dictionary<string, Room>> game in _games)
        {
            if (game.Value.Count == 0) (empty ??= new List<string>()).Add(game.Key);
        }

        if (empty == null) return;
        foreach (string id in empty)
        {
            _games.Remove(id);
            _roomListJson.Remove(id);   // 没有房间了，缓存的空列表也没有留着的理由
        }
    }

    /// <summary>
    /// 诊断用：连接数 / gameId 条目 / 房间总数 / 房间列表缓存。回归测试拿它断言「什么都没留下」。
    /// <para>
    /// 读的是主循环发布出来的快照，不是那几个字典本身 —— 核心的状态是单线程无锁的，
    /// 从别的线程直接去数它会是数据竞争（会读到中间态，也可能直接抛）。
    /// </para>
    /// </summary>
    internal (int Peers, int Games, int Rooms, int ListCache) Snapshot()
    {
        Counts c = _counts;
        return (c.Peers, c.Games, c.Rooms, c.ListCache);
    }

    /// <summary>每处理完一批事件发布一次。单个引用赋值，读到的永远是自洽的一组数。</summary>
    private void PublishCounts() => _counts = new Counts(
        _peers.Count, _games.Count, _games.Values.Sum(r => r.Count), _roomListJson.Count);

    private sealed class Counts
    {
        public Counts(int peers, int games, int rooms, int listCache)
        {
            Peers = peers;
            Games = games;
            Rooms = rooms;
            ListCache = listCache;
        }

        public int Peers { get; }
        public int Games { get; }
        public int Rooms { get; }
        public int ListCache { get; }
    }

    private Dictionary<string, Room> Rooms(string gameId)
    {
        if (!_games.TryGetValue(gameId, out Dictionary<string, Room>? rooms))
        {
            rooms = new Dictionary<string, Room>();
            _games[gameId] = rooms;
        }

        return rooms;
    }

    /// <summary>
    /// Build the room list for a game.
    /// <para>
    /// <b>The serialized result is cached</b> (see <see cref="RoomListJson"/>), so anything
    /// that changes a value read below — player count, capacity, inGame, or which player is
    /// at index 0 — must call <see cref="MarkLobbyDirty"/>. Adding a field here means adding
    /// an invalidation point for it.
    /// </para>
    /// </summary>
    private RoomListMessage RoomsPayload(string gameId)
    {
        if (!_games.TryGetValue(gameId, out Dictionary<string, Room>? rooms))
        {
            return new RoomListMessage(RelayMessageType.RoomList, Array.Empty<RoomInfo>());
        }

        return new RoomListMessage(RelayMessageType.RoomList, rooms.Values.Select(r => new RoomInfo(
            Code: r.Code,
            Name: r.Name,
            PlayerCount: r.Players.Count,   // 纯玩家数，host 不算
            MaxPlayers: r.MaxPlayers,
            InGame: r.InGame,
            HostId: r.Host.Name)).ToArray());
    }

    // ── Room list ─────────────────────────────────────────────────────────────
    //
    // The room list is PULL ONLY: a client asks with LIST_ROOMS and gets one answer. The
    // relay never pushes it unsolicited, and that is a load-bearing decision, not a tuning
    // one.
    //
    // Pushing it does not just cost bandwidth — it cascades. One relay is expected to hold
    // a four-digit number of connections, so every room event would fan a room-sized payload
    // out to every lobby peer. Those payloads queue up inside slow peers' send buffers
    // (the WebSocket outbox is unbounded; a UDP peer force-closes once it has
    // MaxPendingMessages un-acked), and a force-close is itself a room event, which fans out
    // again. Memory growth and disconnects feed each other, so the process dies at a moment
    // that has nothing to do with what anybody was doing. Asking costs the asker; being told
    // costs everyone.
    //
    // What stays is the cache: N clients polling still cost one serialization per change.

    private readonly Dictionary<string, string> _roomListJson = new();

    /// <summary>Room state changed: drop the cached answer so the next asker rebuilds it.</summary>
    private void InvalidateRoomList(string gameId) => _roomListJson.Remove(gameId);

    /// <summary>Answer one peer's LIST_ROOMS from the cache.</summary>
    private void SendRoomList(IRelayConnection connection, string gameId)
    {
        if (!_roomListJson.TryGetValue(gameId, out string? json))
        {
            json = RelayCodec.Serialize(RoomsPayload(gameId));
            _roomListJson[gameId] = json;
        }

        connection.Send(json, DeliveryMode.Reliable);
    }

    private string GenerateCode(string gameId)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        string code;
        do
        {
            code = new string(Enumerable.Range(0, 6).Select(_ => chars[_rng.Next(chars.Length)]).ToArray());
        }
        while (Rooms(gameId).ContainsKey(code));

        return code;
    }

    /// <summary>玩家名单（host 不在其中 —— 它不是玩家）。</summary>
    private static string[] Names(Room room) => room.Players.Select(p => p.Name).ToArray();

    private static bool IsMember(Room room, IRelayConnection connection) =>
        room.Host.Conn == connection || room.Players.Any(p => p.Conn == connection);

    /// <summary>
    /// 发一条消息给房间里所有在线的连接（host + 玩家），可排除某个连接。
    /// <para>
    /// This is the GAME_DATA fan-out path: a host broadcasting state to N players hits it
    /// every tick. The message is serialized once and the same string handed to every
    /// connection — serializing per recipient made an 8-player room do 8× the JSON work
    /// for one broadcast.
    /// </para>
    /// </summary>
    private void SendToRoom(Room room, object message, IRelayConnection? except = null, DeliveryMode mode = DeliveryMode.Reliable)
    {
        string? json = null;
        SendSeat(room.Host);
        foreach (RoomSeat p in room.Players)
        {
            SendSeat(p);
        }

        void SendSeat(RoomSeat seat)
        {
            if (seat.Conn == null || seat.Conn == except) return;

            json ??= RelayCodec.Serialize(message);
            seat.Conn.Send(json, mode);
        }
    }

    /// <summary>房间里没有任何在线连接（host 和玩家都不在）就销毁它（僵尸房间清理）。返回是否销毁。</summary>
    private bool DestroyIfNoLiveMembers(Room room, string gameId)
    {
        if (room.Host.Conn != null || room.Players.Any(p => p.Conn != null)) return false;

        EraseRoom(room, gameId);
        Log($"Room destroyed (no live members): {room.Code}");
        InvalidateRoomList(gameId);
        return true;
    }

    private void Send(IRelayConnection connection, object message, DeliveryMode mode = DeliveryMode.Reliable) =>
        connection.Send(RelayCodec.Serialize(message), mode);

    private bool IsValidToken(string token) =>
        !string.IsNullOrEmpty(token) && FixedTimeEquals(token, _config.Token);

    private static bool FixedTimeEquals(string a, string b)
    {
        // Constant-time comparison so token checking doesn't leak timing information.
        // Not a hard security boundary — the token only deters crawlers, per the project goals.
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }

    private static bool IsValidGameId(string gameId)
    {
        if (gameId.Length == 0 || gameId.Length > 64) return false;
        foreach (char c in gameId)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Per-IP window for bad-token attempts. The token is the real gate; this only slows scripted guessing.</summary>
    private const int BadAuthWindowMs = 60_000;
    private const int BadAuthLimit = 20;

    /// <summary>
    /// Count one bad-token attempt from this address and log when an address gets noisy.
    /// <para>
    /// It deliberately does <b>not</b> refuse later connections from that address. The real
    /// gate is that a bad token closes the connection immediately (see
    /// <see cref="OnRegister"/>), which already leaves a guesser with nothing. Blocking the
    /// address on top of that would punish everyone behind the same NAT, and would turn a
    /// developer's mistyped token into "it worked 20 times then went silent" — exactly the
    /// kind of thing this project exists to keep out of a game developer's day.
    /// </para>
    /// </summary>
    private void Throttle(string address)
    {
        string key = SourceIp(address);
        long now = Environment.TickCount64;
        (int Count, long WindowStartMs) = _badAuth.GetOrAdd(key, (0, now));

        bool windowExpired = now - WindowStartMs > BadAuthWindowMs;
        int count = windowExpired ? 1 : Count + 1;
        _badAuth[key] = windowExpired ? (1, now) : (count, WindowStartMs);

        if (!windowExpired && count == BadAuthLimit + 1)
        {
            Log($"可疑流量：{key} 一分钟内 {count} 次错误 token");
        }

        // Entries are only ever added here, so prune here too — otherwise a scanner
        // sweeping from many addresses grows this dictionary forever on a public relay.
        if (_badAuth.Count > 4096) PruneBadAuth(now);
    }

    private void PruneBadAuth(long now)
    {
        foreach (KeyValuePair<string, (int Count, long WindowStartMs)> entry in _badAuth)
        {
            if (now - entry.Value.WindowStartMs > BadAuthWindowMs)
            {
                _badAuth.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>Address without the port. Behind a reverse proxy this is the proxy's IP, not the client's.</summary>
    private static string SourceIp(string address) => address.Split(':')[0];

    private void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>连接在中继里的位置。没有「未注册」——凭证在连接建立之前就验完了。</summary>
    private enum Loc { Lobby, InRoom }

    private sealed class PeerState
    {
        public Loc Location = Loc.Lobby;
        public string? PlayerId;
        public string? GameId;
        public string? GameCode;
    }

    private sealed class Room
    {
        public string Code = "";
        public string Name = "";

        /// <summary>房主席位。host 不是玩家：不进 <see cref="Players"/>、不占 <see cref="MaxPlayers"/>。</summary>
        public RoomSeat Host = new();

        public List<RoomSeat> Players = new();
        public bool InGame;
        public int MaxPlayers = 4;      // 玩家容量，不含 host
        public bool AutoHostTransfer;   // 房主掉线是否提拔首个在线玩家顶上（玩家侧也带 host 逻辑时才有意义）
        public bool Dedicated;          // 独立部署的 host：拒绝与 host 裸名同名的玩家加入
    }

    private sealed class RoomSeat
    {
        public string Name = "";
        public IRelayConnection? Conn;   // null = 掉线但座位保留，名单仍认它
    }
}
