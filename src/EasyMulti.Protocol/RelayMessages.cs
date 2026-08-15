#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyMulti.Protocol;

// ─────────────────────────────────────────────────────────────────────────────
// EasyMulti wire protocol — the compilable version of docs/PROTOCOL.md.
//
// Two layers, do not conflate them:
//   * This layer = REGISTER / lobby / room / GAME_DATA *envelope*, read by the relay.
//   * The game layer = whatever runs inside GAME_DATA.data, which the relay never
//     parses. data is always an opaque string; the relay just forwards it.
//
// Wire field names are the camelCase of each property name (JsonSerializerDefaults.Web).
// Optional fields whose value is null are omitted from the wire ("absence" = default).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Values of the <c>type</c> field. Receivers read it first, then deserialize the concrete DTO.</summary>
public static class RelayMessageType
{
    // Client → Server
    public const string Register   = "REGISTER";
    public const string ListRooms  = "LIST_ROOMS";
    public const string CreateRoom = "CREATE_ROOM";
    public const string JoinRoom   = "JOIN_ROOM";
    public const string LeaveRoom  = "LEAVE_ROOM";
    public const string StartGame  = "START_GAME";
    public const string GameData   = "GAME_DATA";

    // Server → Client
    public const string RegisterSuccess = "REGISTER_SUCCESS";
    public const string RegisterFailed  = "REGISTER_FAILED";
    public const string RoomList        = "ROOM_LIST";
    public const string LobbyUpdated    = "LOBBY_UPDATED";
    public const string RoomCreated     = "ROOM_CREATED";
    public const string JoinSuccess     = "JOIN_SUCCESS";
    public const string JoinFailed      = "JOIN_FAILED";
    public const string PlayerJoined    = "PLAYER_JOINED";
    public const string PlayerLeft      = "PLAYER_LEFT";
    public const string GameStarted     = "GAME_STARTED";
    public const string LeaveSuccess    = "LEAVE_SUCCESS";
}

// ── Client → Server ──────────────────────────────────────────────────────────

/// <summary>
/// The very first message on any connection. Authenticates with the shared relay
/// token, declares the game this client belongs to (<paramref name="GameId"/>), and
/// registers a <paramref name="PlayerName"/> that is unique within that game.
/// </summary>
public readonly record struct RegisterRequest(string Token, string GameId, string PlayerName)
{
    public string Type => RelayMessageType.Register;
}

/// <summary>Explicitly refresh the room list; the server answers with <see cref="RoomListMessage"/>.</summary>
public readonly record struct ListRoomsRequest
{
    public string Type => RelayMessageType.ListRooms;
}

/// <summary>Create a room. The creator becomes the host (players[0]).</summary>
/// <param name="RoomName">Display name; default "Room".</param>
/// <param name="MaxPlayers">Capacity; default 4.</param>
public readonly record struct CreateRoomRequest(string? RoomName = null, int? MaxPlayers = null)
{
    public string Type => RelayMessageType.CreateRoom;
}

/// <summary>Join a room by its room code.</summary>
public readonly record struct JoinRoomRequest(string GameCode)
{
    public string Type => RelayMessageType.JoinRoom;
}

/// <summary>Leave the current room and return to the lobby.</summary>
public readonly record struct LeaveRoomRequest
{
    public string Type => RelayMessageType.LeaveRoom;
}

/// <summary>Mark the room as "in game". Host only. No game-specific gating is imposed by the relay.</summary>
public readonly record struct StartGameRequest
{
    public string Type => RelayMessageType.StartGame;
}

/// <summary>Submit a game-layer payload. The relay forwards it without parsing.</summary>
/// <param name="Data">Opaque payload (typically base64 of a game-defined encoding).</param>
/// <param name="To">
/// Recipient player name. Omitted → broadcast to every other member of the room;
/// present → delivered only to that player. Never echoed back to the sender.
/// </param>
public readonly record struct GameDataRequest(string Data, string? To = null)
{
    public string Type => RelayMessageType.GameData;
}

// ── Server → Client ──────────────────────────────────────────────────────────

public readonly record struct RegisterSuccessMessage
{
    public string Type => RelayMessageType.RegisterSuccess;
}

/// <summary>Register failed. <see cref="Reason"/> ∈ bad_token / bad_game_id / name_taken / server_full.</summary>
public readonly record struct RegisterFailedMessage(string Reason)
{
    public string Type => RelayMessageType.RegisterFailed;
}

/// <summary>A room summary, element of <see cref="RoomListMessage.Rooms"/>.</summary>
public readonly record struct RoomInfo(
    string Code,
    string Name,
    int PlayerCount,
    int MaxPlayers,
    bool InGame,
    string HostName);

/// <summary>
/// The room list for a game. <see cref="Type"/> is either <c>ROOM_LIST</c> (an answer)
/// or <c>LOBBY_UPDATED</c> (pushed to everyone in the lobby when rooms change). Same shape.
/// </summary>
public readonly record struct RoomListMessage(string Type, RoomInfo[] Rooms);

/// <summary>Room created; the code is server-generated (6 uppercase letters + digits).</summary>
public readonly record struct RoomCreatedMessage(string GameCode)
{
    public string Type => RelayMessageType.RoomCreated;
}

/// <summary>Joined successfully. <see cref="Players"/>[0] is the current host.</summary>
public readonly record struct JoinSuccessMessage(string GameCode, string[] Players)
{
    public string Type => RelayMessageType.JoinSuccess;
}

/// <summary>
/// Join failed. <see cref="Reason"/> ∈ room_not_found / room_full / game_already_started / name_taken.
/// </summary>
public readonly record struct JoinFailedMessage(string Reason)
{
    public string Type => RelayMessageType.JoinFailed;
}

/// <summary>Someone joined; sent to the other room members. <see cref="Players"/> is the full post-join list.</summary>
public readonly record struct PlayerJoinedMessage(string PlayerName, string[] Players)
{
    public string Type => RelayMessageType.PlayerJoined;
}

/// <summary>
/// Someone left (voluntarily or by disconnecting). <see cref="Players"/> is the remaining list;
/// if the host left, <see cref="Players"/>[0] is the new host.
/// </summary>
public readonly record struct PlayerLeftMessage(string PlayerName, string[] Players)
{
    public string Type => RelayMessageType.PlayerLeft;
}

/// <summary>Game started; broadcast to every room member.</summary>
public readonly record struct GameStartedMessage
{
    public string Type => RelayMessageType.GameStarted;
}

/// <summary>Leave acknowledged; the client is back in the lobby. Empty payload.</summary>
public readonly record struct LeaveSuccessMessage
{
    public string Type => RelayMessageType.LeaveSuccess;
}

/// <summary>Forwarded game payload with the source player's name. <see cref="Data"/> is identical to what the sender sent.</summary>
public readonly record struct GameDataMessage(string From, string Data)
{
    public string Type => RelayMessageType.GameData;
}

/// <summary>JSON codec shared by relay, client SDK and host so no one hand-writes field names.</summary>
public static class RelayCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>DTO → wire JSON string.</summary>
    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Read only the <c>type</c> field for dispatch. Returns false if the JSON is not a valid object.</summary>
    public static bool TryReadType(string json, out string type)
    {
        type = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("type", out JsonElement t)) return false;
            if (t.ValueKind != JsonValueKind.String) return false;
            type = t.GetString() ?? "";
            return type.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Wire JSON string → DTO. Missing fields get defaults; malformed input returns false.</summary>
    public static bool TryDeserialize<T>(string json, out T message)
    {
        try
        {
            message = JsonSerializer.Deserialize<T>(json, Options)!;
            return true;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            message = default!;
            return false;
        }
    }
}
