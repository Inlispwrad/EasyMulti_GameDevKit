#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace EasyMultiNet.Protocol
{
    // ─────────────────────────────────────────────────────────────────────────
    // EasyMulti wire protocol — the compilable version of docs/PROTOCOL.md.
    //
    // Two layers, do not conflate them:
    //   * This layer = REGISTER / lobby / room / GAME_DATA *envelope*, read by the relay.
    //   * The game layer = whatever runs inside GAME_DATA.data, which the relay never
    //     parses. data is always an opaque string; the relay just forwards it.
    //
    // Wire field names are the camelCase of each property name. Optional fields whose
    // value is null are omitted from the wire ("absence" = default).
    //
    // These DTOs are `sealed record` (a class) rather than `record struct` because the
    // latter is C# 10 and Unity's compiler stops at C# 9. Usage is identical — value
    // equality, ToString and `with` all still work — at the cost of one small heap
    // allocation per message. That is irrelevant here: every type below is a low-frequency
    // control message. High-frequency game state does not travel through this file.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>协议级命名约定。</summary>
    /// <summary>
    /// 连接凭证的搬运方式。凭证（<see cref="RegisterRequest"/> 的三件套）随**连接请求**一起到，
    /// 中继验完才算连上 —— 没有「连上了但还没证明身份」的中间态，也就没有匿名连接能占住的槽。
    /// <para>
    /// WebSocket 走 <c>Sec-WebSocket-Protocol</c> 子协议名：浏览器的
    /// <c>new WebSocket(url, protocols)</c> 和 C# 的 <c>ClientWebSocket.Options.AddSubProtocol</c>
    /// 都能设它，而自定义 HTTP 头浏览器设不了。凭证因此不进 URL、不落反代 access log。
    /// 子协议名的字符集受 RFC 6455 限制（playerId 允许中文和空格，直接放会非法），所以整包
    /// base64url 编码后再放。
    /// </para>
    /// <para>UDP 没有子协议这一层，凭证 JSON 直接当 HELLO 帧的 payload（见 <c>FrameFlags.Hello</c>）。</para>
    /// </summary>
    public static class RelayHandshake
    {
        /// <summary>握手用的固定子协议名，服务端接受连接时回显它。</summary>
        public const string Protocol = "easymulti";

        /// <summary>凭证子协议名的前缀，形如 <c>em.eyJ0b2tlbiI6...</c>。</summary>
        public const string CredentialPrefix = "em.";

        /// <summary>把凭证编成一个合法的子协议名。</summary>
        public static string Encode(RegisterRequest credentials) =>
            CredentialPrefix + ToBase64Url(Encoding.UTF8.GetBytes(RelayCodec.Serialize(credentials)));

        /// <summary>从子协议名列表里挑出凭证并解开。任何一步不合格都返回 false —— 门口就拦住。</summary>
        public static bool TryDecode(IEnumerable<string>? protocols, out RegisterRequest credentials)
        {
            credentials = default!;
            if (protocols == null) return false;

            foreach (string p in protocols)
            {
                string name = p.Trim();
                if (!name.StartsWith(CredentialPrefix, StringComparison.Ordinal)) continue;

                byte[]? raw = FromBase64Url(name.Substring(CredentialPrefix.Length));
                if (raw == null) return false;
                return RelayCodec.TryDeserialize(Encoding.UTF8.GetString(raw), out credentials);
            }

            return false;
        }

        private static string ToBase64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[]? FromBase64Url(string value)
        {
            string s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: return null;
            }

            try { return Convert.FromBase64String(s); }
            catch (FormatException) { return null; }
        }
    }

    public static class RelayNaming
    {
        /// <summary>
        /// host 连接的注册名后缀（"Alice" 开房的 host 连接叫 "Alice#host"）。
        /// 协议约定而非 SDK 私事：中继按它剥出 host 的裸名，dedicated 房间据此拒绝同名玩家。
        /// </summary>
        public const string HostSuffix = "#host";
    }

    /// <summary>Values of the <c>type</c> field. Receivers read it first, then deserialize the concrete DTO.</summary>
    public static class RelayMessageType
    {
        // Client → Server
        public const string Register   = "REGISTER";
        public const string ListRooms  = "LIST_ROOMS";
        public const string CreateRoom = "CREATE_ROOM";
        public const string JoinRoom   = "JOIN_ROOM";
        public const string LeaveRoom  = "LEAVE_ROOM";
        public const string Kick       = "KICK";
        public const string StartGame  = "START_GAME";

        // Server → Client
        public const string RegisterSuccess = "REGISTER_SUCCESS";
        public const string RegisterFailed  = "REGISTER_FAILED";
        public const string RoomList        = "ROOM_LIST";
        public const string RoomCreated     = "ROOM_CREATED";
        public const string JoinSuccess     = "JOIN_SUCCESS";
        public const string JoinFailed      = "JOIN_FAILED";
        public const string PlayerJoined    = "PLAYER_JOINED";
        public const string PlayerLeft      = "PLAYER_LEFT";
        public const string PlayerDisconnected = "PLAYER_DISCONNECTED";
        public const string PlayerReconnected  = "PLAYER_RECONNECTED";
        public const string HostDropped     = "HOST_DROPPED";
        public const string HostBack        = "HOST_BACK";
        public const string HostChanged     = "HOST_CHANGED";
        public const string GameStarted     = "GAME_STARTED";
        public const string LeaveSuccess    = "LEAVE_SUCCESS";
    }

    // ── Client → Server ──────────────────────────────────────────────────────

    /// <summary>
    /// The very first message on any connection. Authenticates with the shared relay
    /// token, declares the game this client belongs to (<paramref name="GameId"/>), and
    /// registers a <paramref name="PlayerId"/> that is unique within that game.
    /// </summary>
    public sealed record RegisterRequest(string Token, string GameId, string PlayerId)
    {
        public string Type => RelayMessageType.Register;
    }

    /// <summary>Explicitly refresh the room list; the server answers with <see cref="RoomListMessage"/>.</summary>
    public sealed record ListRoomsRequest
    {
        public string Type => RelayMessageType.ListRooms;
    }

    /// <summary>
    /// Create a room. The creator becomes the <b>host</b> — a dedicated connection that is
    /// NOT a player: it never appears in any <c>players</c> array and does not count against
    /// <paramref name="MaxPlayers"/>.
    /// </summary>
    /// <param name="RoomName">Display name; default "Room".</param>
    /// <param name="MaxPlayers">Player capacity (host excluded); default 4.</param>
    /// <param name="AutoHostTransfer">
    /// If true, when the host disconnects the relay promotes the first connected player to be
    /// the new host（他被从玩家名单里提出来，走 HOST_CHANGED）。Only meaningful when player
    /// clients also carry the host logic. If false (default), the host seat is reserved for
    /// reconnection and players see HOST_DROPPED / HOST_BACK.
    /// </param>
    /// <param name="Dedicated">
    /// 声明这是<b>独立部署的 host</b>（专服）：它没有「玩家人格」，因此中继会拒绝与 host
    /// 裸名（注册名去掉 <see cref="RelayNaming.HostSuffix"/>）同名的玩家加入该房间
    /// （JOIN_FAILED: name_reserved）。默认 false ——「自己开房自己玩」的房主本人就叫那个名字，必须放行。
    /// </param>
    public sealed record CreateRoomRequest(string? RoomName = null, int? MaxPlayers = null, bool? AutoHostTransfer = null, bool? Dedicated = null)
    {
        public string Type => RelayMessageType.CreateRoom;
    }

    /// <summary>Join a room by its room code.</summary>
    public sealed record JoinRoomRequest(string GameCode)
    {
        public string Type => RelayMessageType.JoinRoom;
    }

    /// <summary>Leave the current room and return to the lobby.</summary>
    public sealed record LeaveRoomRequest
    {
        public string Type => RelayMessageType.LeaveRoom;
    }

    /// <summary>
    /// Remove a member from the room. Host only. Used to clear a seat whose owner is
    /// permanently gone — deciding who that is, is host logic; the relay just removes.
    /// </summary>
    public sealed record KickRequest(string PlayerId)
    {
        public string Type => RelayMessageType.Kick;
    }

    /// <summary>Mark the room as "in game". Host only. No game-specific gating is imposed by the relay.</summary>
    public sealed record StartGameRequest
    {
        public string Type => RelayMessageType.StartGame;
    }

    // ── Server → Client ──────────────────────────────────────────────────────

    public sealed record RegisterSuccessMessage
    {
        public string Type => RelayMessageType.RegisterSuccess;
    }

    /// <summary>Register failed. <see cref="Reason"/> ∈ bad_token / bad_game_id / name_taken / server_full.</summary>
    public sealed record RegisterFailedMessage(string Reason)
    {
        public string Type => RelayMessageType.RegisterFailed;
    }

    /// <summary>
    /// A room summary, element of <see cref="RoomListMessage.Rooms"/>.
    /// <see cref="PlayerCount"/> / <see cref="MaxPlayers"/> count players only — the host is
    /// a separate connection and never counted.
    /// </summary>
    public sealed record RoomInfo(
        string Code,
        string Name,
        int PlayerCount,
        int MaxPlayers,
        bool InGame,
        string HostId);

    /// <summary>
    /// The room list for a game — always an answer to LIST_ROOMS. The relay never pushes it
    /// unsolicited; see the "Room list" note in RelayServer for why that is load-bearing.
    /// </summary>
    public sealed record RoomListMessage(string Type, RoomInfo[] Rooms);

    /// <summary>Room created; the code is server-generated (6 uppercase letters + digits).</summary>
    public sealed record RoomCreatedMessage(string GameCode)
    {
        public string Type => RelayMessageType.RoomCreated;
    }

    /// <summary>
    /// Joined successfully. <see cref="HostId"/> is the room's host connection;
    /// <see cref="Players"/> is the player list (host excluded).
    /// </summary>
    public sealed record JoinSuccessMessage(string GameCode, string HostId, string[] Players)
    {
        public string Type => RelayMessageType.JoinSuccess;
    }

    /// <summary>
    /// Join failed. <see cref="Reason"/> ∈ room_not_found / room_full / game_already_started /
    /// name_taken / name_reserved（dedicated 房间拒绝与 host 裸名同名的玩家）.
    /// </summary>
    public sealed record JoinFailedMessage(string Reason)
    {
        public string Type => RelayMessageType.JoinFailed;
    }

    /// <summary>Someone joined; sent to the other room members. <see cref="Players"/> is the full post-join list.</summary>
    public sealed record PlayerJoinedMessage(string PlayerId, string[] Players)
    {
        public string Type => RelayMessageType.PlayerJoined;
    }

    /// <summary>Someone left (or was kicked). <see cref="Players"/> is the remaining player list.</summary>
    public sealed record PlayerLeftMessage(string PlayerId, string[] Players)
    {
        public string Type => RelayMessageType.PlayerLeft;
    }

    /// <summary>
    /// A member's connection dropped; their seat is still reserved for reconnection.
    /// <see cref="Players"/> is the full member list (the disconnected member is still in it).
    /// </summary>
    public sealed record PlayerDisconnectedMessage(string PlayerId, string[] Players)
    {
        public string Type => RelayMessageType.PlayerDisconnected;
    }

    /// <summary>A previously disconnected member re-attached to their reserved seat.</summary>
    public sealed record PlayerReconnectedMessage(string PlayerId, string[] Players)
    {
        public string Type => RelayMessageType.PlayerReconnected;
    }

    /// <summary>房主掉线，座位保留等重连。玩家名单不变 —— host 本来就不在里面。</summary>
    public sealed record HostDroppedMessage
    {
        public string Type => RelayMessageType.HostDropped;
    }

    /// <summary>掉线的房主重连坐回来了，对局继续。</summary>
    public sealed record HostBackMessage
    {
        public string Type => RelayMessageType.HostBack;
    }

    /// <summary>
    /// The host role was transferred (auto host transfer): the first connected player was
    /// promoted out of the player list to be the new host. <see cref="HostId"/> is the new
    /// host; <see cref="Players"/> is the remaining player list (promoted player removed).
    /// </summary>
    public sealed record HostChangedMessage(string HostId, string[] Players)
    {
        public string Type => RelayMessageType.HostChanged;
    }

    /// <summary>Game started; broadcast to every room member.</summary>
    public sealed record GameStartedMessage
    {
        public string Type => RelayMessageType.GameStarted;
    }

    /// <summary>Leave acknowledged; the client is back in the lobby. Empty payload.</summary>
    public sealed record LeaveSuccessMessage
    {
        public string Type => RelayMessageType.LeaveSuccess;
    }

    /// <summary>
    /// JSON codec shared by relay, client SDK and host so no one hand-writes field names.
    /// <para>
    /// Mapping is explicit rather than reflective: it keeps the SDK dependency-free (see
    /// <see cref="JsonWriter"/>), makes it immune to IL2CPP stripping, and means renaming a
    /// C# property can never silently change the wire format. Adding a protocol field means
    /// editing the two lines for that message here — by design.
    /// </para>
    /// </summary>
    public static class RelayCodec
    {
        /// <summary>DTO → wire JSON string.</summary>
        public static string Serialize<T>(T message)
        {
            switch (message)
            {
                // Client → Server
                case RegisterRequest m:
                    return Obj().Str("token", m.Token).Str("gameId", m.GameId)
                        .Str("playerId", m.PlayerId).Str("type", m.Type).End();
                case ListRoomsRequest m:
                    return Obj().Str("type", m.Type).End();
                case CreateRoomRequest m:
                    return Obj().Str("roomName", m.RoomName).Num("maxPlayers", m.MaxPlayers)
                        .Bool("autoHostTransfer", m.AutoHostTransfer).Bool("dedicated", m.Dedicated)
                        .Str("type", m.Type).End();
                case JoinRoomRequest m:
                    return Obj().Str("gameCode", m.GameCode).Str("type", m.Type).End();
                case LeaveRoomRequest m:
                    return Obj().Str("type", m.Type).End();
                case KickRequest m:
                    return Obj().Str("playerId", m.PlayerId).Str("type", m.Type).End();
                case StartGameRequest m:
                    return Obj().Str("type", m.Type).End();
                // Server → Client
                case RegisterSuccessMessage m:
                    return Obj().Str("type", m.Type).End();
                case RegisterFailedMessage m:
                    return Obj().Str("reason", m.Reason).Str("type", m.Type).End();
                case RoomInfo m:
                    return WriteRoomInfo(m);
                case RoomListMessage m:
                    return Obj().Str("type", m.Type).ObjArray("rooms", m.Rooms, WriteRoomInfo).End();
                case RoomCreatedMessage m:
                    return Obj().Str("gameCode", m.GameCode).Str("type", m.Type).End();
                case JoinSuccessMessage m:
                    return Obj().Str("gameCode", m.GameCode).Str("hostId", m.HostId)
                        .StrArray("players", m.Players).Str("type", m.Type).End();
                case JoinFailedMessage m:
                    return Obj().Str("reason", m.Reason).Str("type", m.Type).End();
                case PlayerJoinedMessage m:
                    return Obj().Str("playerId", m.PlayerId).StrArray("players", m.Players)
                        .Str("type", m.Type).End();
                case PlayerLeftMessage m:
                    return Obj().Str("playerId", m.PlayerId).StrArray("players", m.Players)
                        .Str("type", m.Type).End();
                case PlayerDisconnectedMessage m:
                    return Obj().Str("playerId", m.PlayerId).StrArray("players", m.Players)
                        .Str("type", m.Type).End();
                case PlayerReconnectedMessage m:
                    return Obj().Str("playerId", m.PlayerId).StrArray("players", m.Players)
                        .Str("type", m.Type).End();
                case HostDroppedMessage m:
                    return Obj().Str("type", m.Type).End();
                case HostBackMessage m:
                    return Obj().Str("type", m.Type).End();
                case HostChangedMessage m:
                    return Obj().Str("hostId", m.HostId).StrArray("players", m.Players)
                        .Str("type", m.Type).End();
                case GameStartedMessage m:
                    return Obj().Str("type", m.Type).End();
                case LeaveSuccessMessage m:
                    return Obj().Str("type", m.Type).End();

                default:
                    throw new ArgumentException(
                        "没有为 " + (message?.GetType().Name ?? "null") + " 登记编码规则，"
                        + "新增消息类型时要同时改 RelayCodec.Serialize 和 ReadMessage",
                        nameof(message));
            }
        }

        /// <summary>Read only the <c>type</c> field for dispatch. Returns false if the JSON is not a valid object.</summary>
        public static bool TryReadType(string json, out string type)
        {
            type = "";
            if (!JsonValue.TryParse(json, out JsonValue root) || !root.IsObject) return false;
            type = root.Str("type");
            return type.Length > 0;
        }

        /// <summary>Wire JSON string → DTO. Missing fields get defaults; malformed input returns false.</summary>
        public static bool TryDeserialize<T>(string json, out T message)
        {
            message = default!;
            if (!JsonValue.TryParse(json, out JsonValue root) || !root.IsObject) return false;

            object? parsed = ReadMessage(typeof(T), root);
            if (parsed == null) return false;

            message = (T)parsed;
            return true;
        }

        private static JsonWriter Obj() => new JsonWriter().Begin();

        private static string WriteRoomInfo(RoomInfo r) =>
            new JsonWriter().Begin()
                .Str("code", r.Code)
                .Str("name", r.Name)
                .Num("playerCount", r.PlayerCount)
                .Num("maxPlayers", r.MaxPlayers)
                .Bool("inGame", r.InGame)
                .Str("hostId", r.HostId)
                .End();

        private static RoomInfo ReadRoomInfo(JsonValue o) => new RoomInfo(
            o.Str("code"),
            o.Str("name"),
            o.Int("playerCount"),
            o.Int("maxPlayers"),
            o.Bool("inGame"),
            o.Str("hostId"));

        /// <summary>Only the messages a receiver actually parses are listed; the rest are dispatched by type alone.</summary>
        private static object? ReadMessage(Type type, JsonValue o)
        {
            // Client → Server (read by the relay)
            if (type == typeof(RegisterRequest))
                return new RegisterRequest(o.Str("token"), o.Str("gameId"), o.Str("playerId"));
            if (type == typeof(CreateRoomRequest))
                return new CreateRoomRequest(o.OptStr("roomName"), o.OptInt("maxPlayers"), o.OptBool("autoHostTransfer"), o.OptBool("dedicated"));
            if (type == typeof(JoinRoomRequest))
                return new JoinRoomRequest(o.Str("gameCode"));
            if (type == typeof(KickRequest))
                return new KickRequest(o.Str("playerId"));

            // Server → Client (read by the SDK)
            if (type == typeof(RegisterFailedMessage))
                return new RegisterFailedMessage(o.Str("reason"));
            if (type == typeof(RoomListMessage))
                return new RoomListMessage(o.Str("type"), o.ObjArray("rooms", ReadRoomInfo));
            if (type == typeof(RoomCreatedMessage))
                return new RoomCreatedMessage(o.Str("gameCode"));
            if (type == typeof(JoinSuccessMessage))
                return new JoinSuccessMessage(o.Str("gameCode"), o.Str("hostId"), o.StrArray("players"));
            if (type == typeof(JoinFailedMessage))
                return new JoinFailedMessage(o.Str("reason"));
            if (type == typeof(PlayerJoinedMessage))
                return new PlayerJoinedMessage(o.Str("playerId"), o.StrArray("players"));
            if (type == typeof(PlayerLeftMessage))
                return new PlayerLeftMessage(o.Str("playerId"), o.StrArray("players"));
            if (type == typeof(PlayerDisconnectedMessage))
                return new PlayerDisconnectedMessage(o.Str("playerId"), o.StrArray("players"));
            if (type == typeof(PlayerReconnectedMessage))
                return new PlayerReconnectedMessage(o.Str("playerId"), o.StrArray("players"));
            if (type == typeof(HostChangedMessage))
                return new HostChangedMessage(o.Str("hostId"), o.StrArray("players"));

            return null;
        }
    }
}
