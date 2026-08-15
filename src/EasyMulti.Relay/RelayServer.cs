#nullable enable

using System.Collections.Concurrent;
using EasyMulti.Protocol;
using EasyMulti.Relay.Transport;

namespace EasyMulti.Relay;

/// <summary>
/// The relay core. Owns the game/room state and the single-threaded event loop.
/// <para>
/// All I/O (WebSocket pumps, UDP receive loop) funnels through <see cref="_events"/>;
/// the main loop dequeues and dispatches, so every handler below runs on one thread and
/// needs no locks. The relay only forwards <c>GAME_DATA.data</c> — it never parses it.
/// </para>
/// <para>
/// Reconnection: a member whose connection drops keeps their <b>seat</b> in the room for
/// <c>ReconnectGraceMs</c>. During that window they can re-register with the same
/// playerName and JOIN_ROOM(code) to re-attach to their reserved seat — even if the game
/// already started. After the grace expires the seat is freed for real.
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
            transport.Start(Enqueue);
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
            while (_events.TryDequeue(out RelayEvent e))
            {
                Dispatch(e);
            }

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

    // ── Event dispatch ────────────────────────────────────────────────────────

    private void Dispatch(RelayEvent e)
    {
        switch (e.Kind)
        {
            case RelayEventKind.Connected:
                OnConnected(e.Connection);
                break;

            case RelayEventKind.Message:
                if (e.Text != null && _peers.TryGetValue(e.Connection, out PeerState? state))
                {
                    OnMessage(e.Connection, state, e.Text, e.Mode);
                }

                break;

            case RelayEventKind.Disconnected:
                OnDisconnected(e.Connection, e.Reason);
                break;
        }
    }

    private void OnConnected(IRelayConnection connection)
    {
        if (_peers.Count >= _config.MaxConnections)
        {
            Log($"拒绝连接 {connection.Address}：达到最大连接数 {_config.MaxConnections}");
            connection.Close("server_full");
            return;
        }

        _peers[connection] = new PeerState();
        Log($"Peer connected: {connection.Address} [{connection.TransportName}]");
    }

    private void OnDisconnected(IRelayConnection connection, string reason)
    {
        if (!_peers.TryGetValue(connection, out PeerState? state))
        {
            return;
        }

        string who = state.PlayerName ?? "（未注册）";
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

        if (state.Location == Loc.Unregistered)
        {
            if (type == RelayMessageType.Register)
            {
                OnRegister(connection, state, json);
            }

            return;
        }

        switch (type)
        {
            case RelayMessageType.ListRooms:  Send(connection, RoomsPayload(state.GameId!, RelayMessageType.RoomList)); break;
            case RelayMessageType.CreateRoom: OnCreateRoom(connection, state, json); break;
            case RelayMessageType.JoinRoom:   OnJoinRoom(connection, state, json);   break;
            case RelayMessageType.LeaveRoom:  LeaveRoom(connection, state);         break;
            case RelayMessageType.Kick:       OnKick(connection, state, json);      break;
            case RelayMessageType.StartGame:  OnStartGame(connection, state);       break;
            case RelayMessageType.GameData:   OnGameData(connection, state, json, mode); break;
        }
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private void OnRegister(IRelayConnection connection, PeerState state, string json)
    {
        if (!RelayCodec.TryDeserialize<RegisterRequest>(json, out RegisterRequest req))
        {
            return;
        }

        string gameId = req.GameId.Trim();
        string playerName = req.PlayerName.Trim();

        if (!IsValidToken(req.Token))
        {
            Log($"注册被拒：token 无效（{connection.Address}）");
            Send(connection, new RegisterFailedMessage("bad_token"));
            Throttle(connection);
            connection.Close("bad_token");
            return;
        }

        if (!IsValidGameId(gameId))
        {
            Send(connection, new RegisterFailedMessage("bad_game_id"));
            connection.Close("bad_game_id");
            return;
        }

        if (playerName.Length == 0 || playerName.Length > 64)
        {
            Send(connection, new RegisterFailedMessage("bad_request"));
            connection.Close("bad_request");
            return;
        }

        if (_peers.Count >= _config.MaxConnections)
        {
            Send(connection, new RegisterFailedMessage("server_full"));
            connection.Close("server_full");
            return;
        }

        if (_peers.Values.Any(s =>
                s.PlayerName == playerName && string.Equals(s.GameId, gameId, StringComparison.Ordinal)))
        {
            Send(connection, new RegisterFailedMessage("name_taken"));
            return;
        }

        state.PlayerName = playerName;
        state.GameId = gameId;
        state.Location = Loc.Lobby;
        Log($"Player registered: {playerName} @ {gameId} ({connection.Address})");
        Send(connection, new RegisterSuccessMessage());
        Send(connection, RoomsPayload(gameId, RelayMessageType.RoomList));
    }

    private void OnCreateRoom(IRelayConnection connection, PeerState state, string json)
    {
        if (state.Location != Loc.Lobby) return;
        if (!RelayCodec.TryDeserialize<CreateRoomRequest>(json, out CreateRoomRequest req)) return;

        string roomName = string.IsNullOrWhiteSpace(req.RoomName) ? "Room" : req.RoomName!;
        int maxPlayers = req.MaxPlayers ?? 4;
        if (maxPlayers < 2) maxPlayers = 2;
        if (maxPlayers > 1024) maxPlayers = 1024;

        string gameId = state.GameId!;
        string code = GenerateCode(gameId);
        var room = new Room { Code = code, Name = roomName, MaxPlayers = maxPlayers };
        room.Players.Add(new RoomPlayer { Name = state.PlayerName!, Conn = connection });

        Rooms(gameId)[code] = room;
        state.Location = Loc.InRoom;
        state.GameCode = code;

        Log($"Room created: {code} '{roomName}' by {state.PlayerName} @ {gameId}");
        Send(connection, new RoomCreatedMessage(code));
        BroadcastLobbyUpdated(gameId);
    }

    private void OnJoinRoom(IRelayConnection connection, PeerState state, string json)
    {
        if (state.Location != Loc.Lobby) return;
        if (!RelayCodec.TryDeserialize<JoinRoomRequest>(json, out JoinRoomRequest req)) return;

        string gameId = state.GameId!;
        string code = req.GameCode;
        string playerName = state.PlayerName!;
        if (string.IsNullOrEmpty(code)) return;

        if (!Rooms(gameId).TryGetValue(code, out Room? room))
        {
            Send(connection, new JoinFailedMessage("room_not_found"));
            return;
        }

        // 重连：有同名保留座位 → 直接坐回（无论房间是否开局）。名单即准入，无时限。
        RoomPlayer? seat = room.Players.FirstOrDefault(p => p.Name == playerName && p.Conn == null);
        if (seat != null)
        {
            seat.Conn = connection;
            state.Location = Loc.InRoom;
            state.GameCode = code;

            Send(connection, new JoinSuccessMessage(code, Names(room)));
            if (room.InGame) Send(connection, new GameStartedMessage());
            SendToRoom(room, new PlayerReconnectedMessage(playerName, Names(room)), except: connection);
            Log($"Peer reconnected to room {code}: {playerName} @ {gameId}");
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

        if (room.Players.Any(p => p.Name == playerName))
        {
            Send(connection, new JoinFailedMessage("name_taken"));
            return;
        }

        room.Players.Add(new RoomPlayer { Name = playerName, Conn = connection });
        state.Location = Loc.InRoom;
        state.GameCode = code;

        Send(connection, new JoinSuccessMessage(code, Names(room)));
        SendToRoom(room, new PlayerJoinedMessage(playerName, Names(room)), except: connection);
        Log($"Peer joined room {code}: {playerName} @ {gameId}");
        BroadcastLobbyUpdated(gameId);
    }

    private void LeaveRoom(IRelayConnection connection, PeerState state)
    {
        if (state.Location != Loc.InRoom || state.GameCode is null) return;
        if (!Rooms(state.GameId!).TryGetValue(state.GameCode, out Room? room)) return;

        string leavingName = state.PlayerName ?? "Unknown";
        string code = state.GameCode;
        room.Players.RemoveAll(p => p.Conn == connection);
        state.Location = Loc.Lobby;
        state.GameCode = null;

        if (room.Players.Count == 0)
        {
            Rooms(state.GameId!).Remove(code);
            Log($"Room destroyed: {code}");
        }
        else
        {
            // 房主离开时 players[0] 自动顺延（列表移位），无需显式迁移。
            SendToRoom(room, new PlayerLeftMessage(leavingName, Names(room)));
        }

        Send(connection, new LeaveSuccessMessage());
        Send(connection, RoomsPayload(state.GameId!, RelayMessageType.RoomList));
        BroadcastLobbyUpdated(state.GameId!);
    }

    private void OnStartGame(IRelayConnection connection, PeerState state)
    {
        if (state.Location != Loc.InRoom || state.GameCode is null) return;
        if (!Rooms(state.GameId!).TryGetValue(state.GameCode, out Room? room)) return;
        if (room.Players.Count == 0 || room.Players[0].Conn != connection) return; // 只有房主能开

        room.InGame = true;
        Log($"Game started in room {state.GameCode}");
        SendToRoom(room, new GameStartedMessage());
        BroadcastLobbyUpdated(state.GameId!);
    }

    private void OnGameData(IRelayConnection connection, PeerState state, string json, DeliveryMode mode)
    {
        if (state.Location != Loc.InRoom || state.GameCode is null) return;
        if (!Rooms(state.GameId!).TryGetValue(state.GameCode, out Room? room)) return;
        if (!IsMember(room, connection)) return; // 硬约束：只有房间成员能发 GAME_DATA
        if (!RelayCodec.TryDeserialize<GameDataRequest>(json, out GameDataRequest req)) return;

        string sender = state.PlayerName ?? "Unknown";
        var fwd = new GameDataMessage(sender, req.Data ?? "");

        if (!string.IsNullOrEmpty(req.To))
        {
            RoomPlayer? target = room.Players.FirstOrDefault(p => p.Name == req.To && p.Conn != null && p.Conn != connection);
            if (target != null)
            {
                Send(target.Conn!, fwd, mode);
            }

            return;
        }

        SendToRoom(room, fwd, except: connection, mode: mode);
    }

    // ── Reconnection & removal ───────────────────────────────────────────────

    /// <summary>把断开连接的成员标记为「保留座位」，等它同名重连。无时限，移除由 Host 决定。</summary>
    private void ReserveSeat(IRelayConnection connection, PeerState state)
    {
        if (state.GameCode is null) return;
        if (!Rooms(state.GameId!).TryGetValue(state.GameCode, out Room? room)) return;

        RoomPlayer? seat = room.Players.FirstOrDefault(p => p.Conn == connection);
        if (seat == null) return;

        seat.Conn = null;
        Log($"Seat reserved for reconnect: {seat.Name} in room {room.Code}");
        SendToRoom(room, new PlayerDisconnectedMessage(seat.Name, Names(room)));
    }

    /// <summary>房主踢人：把指定成员从名单移除（在线则送回大厅），通知其余人。</summary>
    private void OnKick(IRelayConnection connection, PeerState state, string json)
    {
        if (state.Location != Loc.InRoom || state.GameCode is null) return;
        if (!Rooms(state.GameId!).TryGetValue(state.GameCode, out Room? room)) return;
        if (room.Players.Count == 0 || room.Players[0].Conn != connection) return; // 只有房主能踢
        if (!RelayCodec.TryDeserialize<KickRequest>(json, out KickRequest req)) return;

        string targetName = req.PlayerName;
        if (targetName == room.Players[0].Name) return; // 不能踢房主自己
        RoomPlayer? target = room.Players.FirstOrDefault(p => p.Name == targetName);
        if (target == null) return;

        RemoveSeat(room, target, state.GameId!);
    }

    /// <summary>把某个座位从房间移除：在线者送回大厅，其余人收 PLAYER_LEFT。</summary>
    private void RemoveSeat(Room room, RoomPlayer target, string gameId)
    {
        room.Players.Remove(target);
        Log($"Seat removed: {target.Name} in room {room.Code}");

        // 目标在线 → 送回大厅。
        if (target.Conn != null && _peers.TryGetValue(target.Conn, out PeerState? targetState))
        {
            targetState.Location = Loc.Lobby;
            targetState.GameCode = null;
            Send(target.Conn, new LeaveSuccessMessage());
            Send(target.Conn, RoomsPayload(gameId, RelayMessageType.RoomList));
        }

        if (room.Players.Count == 0)
        {
            Rooms(gameId).Remove(room.Code);
            Log($"Room destroyed: {room.Code}");
        }
        else
        {
            SendToRoom(room, new PlayerLeftMessage(target.Name, Names(room)));
        }

        BroadcastLobbyUpdated(gameId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Dictionary<string, Room> Rooms(string gameId)
    {
        if (!_games.TryGetValue(gameId, out Dictionary<string, Room>? rooms))
        {
            rooms = new Dictionary<string, Room>();
            _games[gameId] = rooms;
        }

        return rooms;
    }

    private RoomListMessage RoomsPayload(string gameId, string type)
    {
        if (!_games.TryGetValue(gameId, out Dictionary<string, Room>? rooms))
        {
            return new RoomListMessage(type, Array.Empty<RoomInfo>());
        }

        return new RoomListMessage(type, rooms.Values.Select(r => new RoomInfo(
            Code: r.Code,
            Name: r.Name,
            PlayerCount: r.Players.Count,
            MaxPlayers: r.MaxPlayers,
            InGame: r.InGame,
            HostName: r.Players.Count > 0 ? r.Players[0].Name : "")).ToArray());
    }

    private void BroadcastLobbyUpdated(string gameId)
    {
        var payload = RoomsPayload(gameId, RelayMessageType.LobbyUpdated);
        foreach ((IRelayConnection conn, PeerState s) in _peers)
        {
            if (s.Location == Loc.Lobby && string.Equals(s.GameId, gameId, StringComparison.Ordinal))
            {
                Send(conn, payload);
            }
        }
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

    private static string[] Names(Room room) => room.Players.Select(p => p.Name).ToArray();

    private static bool IsMember(Room room, IRelayConnection connection) =>
        room.Players.Any(p => p.Conn == connection);

    /// <summary>发一条消息给房间里所有在线的成员，可排除某个连接。</summary>
    private void SendToRoom(Room room, object message, IRelayConnection? except = null, DeliveryMode mode = DeliveryMode.Reliable)
    {
        foreach (RoomPlayer p in room.Players)
        {
            if (p.Conn != null && p.Conn != except)
            {
                Send(p.Conn, message, mode);
            }
        }
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

    private void Throttle(IRelayConnection connection)
    {
        // Simple per-IP counter: too many bad tokens from one address → back off.
        // The token itself is the real gate; this only slows down scripted guessing.
        string key = connection.Address.Split(':')[0];
        long now = Environment.TickCount64;
        (int Count, long WindowStartMs) = _badAuth.GetOrAdd(key, (0, now));
        if (now - WindowStartMs > 60_000)
        {
            _badAuth[key] = (1, now);
        }
        else if (Count > 20)
        {
            Log($"可疑流量：{key} 短时间大量错误 token，连接被拒绝");
            connection.Close("rate_limited");
        }
        else
        {
            _badAuth[key] = (Count + 1, WindowStartMs);
        }
    }

    private void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    // ── State ─────────────────────────────────────────────────────────────────

    private enum Loc { Unregistered, Lobby, InRoom }

    private sealed class PeerState
    {
        public Loc Location = Loc.Unregistered;
        public string? PlayerName;
        public string? GameId;
        public string? GameCode;
    }

    private sealed class Room
    {
        public string Code = "";
        public string Name = "";
        public List<RoomPlayer> Players = new();
        public bool InGame;
        public int MaxPlayers = 4;
    }

    private sealed class RoomPlayer
    {
        public string Name = "";
        public IRelayConnection? Conn;   // null = 掉线但座位保留，名单仍认它
    }
}
