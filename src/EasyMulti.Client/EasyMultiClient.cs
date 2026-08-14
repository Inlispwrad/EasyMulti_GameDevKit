#nullable enable

using EasyMulti.Protocol;

namespace EasyMulti.Client;

public enum EasyMultiState
{
    Disconnected,
    Connecting,
    Unregistered,
    Lobby,
    InRoom,
    InGame,
}

/// <summary>
/// The client state machine for the EasyMulti relay: connect → REGISTER → create/join a
/// room → START_GAME → exchange GAME_DATA. Works for both players and hosts — a host is
/// simply the client that calls <see cref="CreateRoom"/> (and therefore is players[0]).
/// <para>
/// <b>Transport-agnostic and game-agnostic:</b> it depends only on
/// <see cref="IClientTransport"/> and treats <c>GAME_DATA.data</c> as an opaque string.
/// Single-threaded — <see cref="Poll"/> runs on the caller's loop and all events fire
/// inside it.
/// </para>
/// </summary>
public sealed class EasyMultiClient : IDisposable
{
    private readonly IClientTransport _transport;
    private readonly List<string> _roomPlayers = new();
    private readonly List<RoomInfo> _rooms = new();
    private bool _open;

    public EasyMultiClient(EasyMultiConfig config, IClientTransport transport)
    {
        Config = config;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.Opened += OnOpened;
        _transport.Closed += OnClosed;
        _transport.Received += OnReceived;
    }

    /// <summary>Convenience: WebSocket-backed client.</summary>
    public static EasyMultiClient CreateWebSocket(EasyMultiConfig config) =>
        new(config, new WebSocketClientTransport());

    /// <summary>Convenience: UDP-backed client.</summary>
    public static EasyMultiClient CreateUdp(EasyMultiConfig config) =>
        new(config, new UdpClientTransport());

    public EasyMultiConfig Config { get; }

    public EasyMultiState State { get; private set; } = EasyMultiState.Disconnected;

    /// <summary>Current room code; null when not in a room.</summary>
    public string? GameCode { get; private set; }

    /// <summary>Room members, [0] is the host. Empty outside a room.</summary>
    public IReadOnlyList<string> RoomPlayers => _roomPlayers;

    /// <summary>Current lobby snapshot for this game.</summary>
    public IReadOnlyList<RoomInfo> Rooms => _rooms;

    public string? HostName => _roomPlayers.Count > 0 ? _roomPlayers[0] : null;

    public bool IsHost => _roomPlayers.Count > 0 && _roomPlayers[0] == Config.PlayerName;

    // ── Events (all fired inside Poll) ────────────────────────────────────────

    public event Action? Registered;
    public event Action<IReadOnlyList<RoomInfo>>? RoomListChanged;
    public event Action<string>? RoomCreated;
    public event Action<string>? RoomJoined;
    public event Action<IReadOnlyList<string>>? RoomPlayersChanged;
    public event Action? GameStarted;
    public event Action<string, string>? GameDataReceived;
    public event Action<string>? Failed;

    // ── Outbound ──────────────────────────────────────────────────────────────

    /// <summary>Connect to the relay. On success REGISTER is sent automatically.</summary>
    public void Connect(string host, int port)
    {
        if (State != EasyMultiState.Disconnected) throw new InvalidOperationException("已经连过了");
        State = EasyMultiState.Connecting;
        _transport.Connect(host, port);
    }

    /// <summary>Drive the transport and fire events. Call every 10–20 ms.</summary>
    public void Poll() => _transport.Poll();

    public void RefreshRooms() => Send(new ListRoomsRequest());

    public void CreateRoom(string? roomName = null, int? maxPlayers = null) =>
        Send(new CreateRoomRequest(roomName, maxPlayers));

    public void JoinRoom(string gameCode) => Send(new JoinRoomRequest(gameCode));

    public void LeaveRoom() => Send(new LeaveRoomRequest());

    /// <summary>Mark the room as in-game. Host only.</summary>
    public void StartGame() => Send(new StartGameRequest());

    /// <summary>Send one game-layer payload. Never echoed back to the sender.</summary>
    public void SendGameData(string data, string? to = null, DeliveryMode mode = DeliveryMode.Reliable) =>
        Send(new GameDataRequest(data, to), mode);

    public void Dispose()
    {
        _open = false;
        _transport.Dispose();
        State = EasyMultiState.Disconnected;
    }

    // ── Inbound ───────────────────────────────────────────────────────────────

    private void OnOpened()
    {
        _open = true;
        State = EasyMultiState.Unregistered;
        Send(new RegisterRequest(Config.Token, Config.GameId, Config.PlayerName));
    }

    private void OnClosed(string reason)
    {
        _open = false;
        State = EasyMultiState.Disconnected;
        _roomPlayers.Clear();
        _rooms.Clear();
        GameCode = null;
        Failed?.Invoke(reason);
    }

    private void OnReceived(string json, DeliveryMode mode)
    {
        if (!RelayCodec.TryReadType(json, out string type)) return;

        switch (type)
        {
            case RelayMessageType.RegisterSuccess:
                State = EasyMultiState.Lobby;
                Registered?.Invoke();
                break;

            case RelayMessageType.RegisterFailed:
                if (RelayCodec.TryDeserialize(json, out RegisterFailedMessage regFail))
                {
                    Fail("注册失败：" + regFail.Reason);
                }

                break;

            case RelayMessageType.RoomList:
            case RelayMessageType.LobbyUpdated:
                if (RelayCodec.TryDeserialize(json, out RoomListMessage list))
                {
                    SetRooms(list.Rooms);
                }

                break;

            case RelayMessageType.RoomCreated:
                if (RelayCodec.TryDeserialize(json, out RoomCreatedMessage created))
                {
                    GameCode = created.GameCode;
                    State = EasyMultiState.InRoom;
                    SetRoomPlayers(new[] { Config.PlayerName });
                    RoomCreated?.Invoke(created.GameCode);
                }

                break;

            case RelayMessageType.JoinSuccess:
                if (RelayCodec.TryDeserialize(json, out JoinSuccessMessage joined))
                {
                    GameCode = joined.GameCode;
                    State = EasyMultiState.InRoom;
                    SetRoomPlayers(joined.Players);
                    RoomJoined?.Invoke(joined.GameCode);
                }

                break;

            case RelayMessageType.JoinFailed:
                if (RelayCodec.TryDeserialize(json, out JoinFailedMessage joinFail))
                {
                    Fail("加入房间失败：" + joinFail.Reason);
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

            case RelayMessageType.GameStarted:
                State = EasyMultiState.InGame;
                GameStarted?.Invoke();
                break;

            case RelayMessageType.GameData:
                if (RelayCodec.TryDeserialize(json, out GameDataMessage data))
                {
                    GameDataReceived?.Invoke(data.From, data.Data);
                }

                break;
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

    private void Fail(string reason)
    {
        // Terminal for the current session intent, but keep the connection open so the
        // caller can react (e.g. re-register under a different name).
        Failed?.Invoke(reason);
    }

    private void Send(object message, DeliveryMode mode = DeliveryMode.Reliable)
    {
        if (!_open) throw new InvalidOperationException("还没连上中继");
        _transport.Send(RelayCodec.Serialize(message), mode);
    }
}
