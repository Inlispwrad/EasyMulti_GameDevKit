using System.Text;
using System.Text.Json;
using EasyMultiNet.Protocol;
using Xunit;

namespace EasyMultiNet.Tests;

/// <summary>
/// The protocol carries its own JSON codec so the SDK stays a zero-dependency source drop.
/// That trade only holds if the codec is provably correct, so these tests use
/// <see cref="System.Text.Json"/> — available here because the test project is net8.0, and
/// deliberately *not* referenced by the SDK — as an independent oracle in both directions:
/// what we write must parse as valid JSON with the right fields, and what a foreign writer
/// produces must read back correctly.
/// </summary>
public class JsonCodecTests
{
    // ── Writer: output is valid JSON with the expected fields ────────────────

    [Fact]
    public void EveryMessage_SerializesToValidJson()
    {
        foreach (object message in AllMessages())
        {
            string json = RelayCodec.Serialize(message);
            // Throws if the codec emitted anything malformed.
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
    }

    [Fact]
    public void RegisterRequest_HasExactWireShape()
    {
        string json = RelayCodec.Serialize(new RegisterRequest("tok", "my-game", "Alice"));
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal("tok", root.GetProperty("token").GetString());
        Assert.Equal("my-game", root.GetProperty("gameId").GetString());
        Assert.Equal("Alice", root.GetProperty("playerId").GetString());
        Assert.Equal("REGISTER", root.GetProperty("type").GetString());
        Assert.Equal(4, CountProperties(root));
    }

    [Fact]
    public void RoomList_NestsRoomObjects()
    {
        var message = new RoomListMessage(
            RelayMessageType.RoomList,
            new[]
            {
                new RoomInfo("ABC123", "第一间", 2, 4, false, "Host"),
                new RoomInfo("XYZ789", "Room 2", 4, 4, true, "Bob"),
            });

        using JsonDocument doc = JsonDocument.Parse(RelayCodec.Serialize(message));
        JsonElement rooms = doc.RootElement.GetProperty("rooms");

        Assert.Equal(2, rooms.GetArrayLength());
        Assert.Equal("ABC123", rooms[0].GetProperty("code").GetString());
        Assert.Equal("第一间", rooms[0].GetProperty("name").GetString());
        Assert.Equal(2, rooms[0].GetProperty("playerCount").GetInt32());
        Assert.False(rooms[0].GetProperty("inGame").GetBoolean());
        Assert.True(rooms[1].GetProperty("inGame").GetBoolean());
    }

    [Fact]
    public void EmptyRoomList_SerializesAsEmptyArray()
    {
        string json = RelayCodec.Serialize(new RoomListMessage(RelayMessageType.RoomList, System.Array.Empty<RoomInfo>()));
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("rooms").GetArrayLength());
    }

    [Fact]
    public void NullOptionalFields_AreOmittedFromTheWire()
    {
        // "absence = default" per docs/PROTOCOL.md — a null must not become "roomName": null.
        using JsonDocument all = JsonDocument.Parse(RelayCodec.Serialize(new CreateRoomRequest()));
        Assert.Equal(1, CountProperties(all.RootElement)); // just "type"

        using JsonDocument some = JsonDocument.Parse(RelayCodec.Serialize(new CreateRoomRequest("R")));
        Assert.Equal("R", some.RootElement.GetProperty("roomName").GetString());
        Assert.False(some.RootElement.TryGetProperty("maxPlayers", out _));
    }

    // ── Escaping: the one place a hand-written writer usually breaks ──────────

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("引号 \" 在中间")]
    [InlineData("反斜杠 \\ 和 \\\" 组合")]
    [InlineData("换行\n回车\r制表\t")]
    [InlineData("退格\b换页\f")]
    [InlineData("控制字符\u0001\u001f")]
    [InlineData("中文房间名")]
    [InlineData("emoji 🎮🕹️ 代理对")]
    [InlineData("Bgf6/78/ABDAMw==")]          // base64 with '/'
    [InlineData("BgcAAAAAAMA/AAAQwDMzMz9dAAAA+v///wUAAABBbGljZQ==")] // base64 with '+' and '/'
    [InlineData("混合 \" \\ \n 中文 🎮 +/= 全都有")]
    public void Strings_SurviveWriteThenForeignRead(string value)
    {
        string json = RelayCodec.Serialize(new RoomCreatedMessage(value));

        // Read back with an independent parser: proves our escaping is real JSON.
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(value, doc.RootElement.GetProperty("gameCode").GetString());

        // And with our own parser: proves write/read agree.
        Assert.True(RelayCodec.TryDeserialize(json, out RoomCreatedMessage back));
        Assert.Equal(value, back.GameCode);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("引号 \" 在中间")]
    [InlineData("换行\n回车\r制表\t")]
    [InlineData("中文房间名")]
    [InlineData("emoji 🎮🕹️ 代理对")]
    [InlineData("斜杠 / 与 \\u 字面量")]
    public void Strings_SurviveForeignWriteThenOurRead(string value)
    {
        // System.Text.Json escapes non-ASCII as \uXXXX (and emoji as surrogate pairs of
        // escapes). Our parser has to decode all of that.
        string foreign = JsonSerializer.Serialize(new { gameCode = value, type = "ROOM_CREATED" });

        Assert.True(RelayCodec.TryDeserialize(foreign, out RoomCreatedMessage back));
        Assert.Equal(value, back.GameCode);
    }

    [Fact]
    public void NonAsciiIsNotEscaped_SoTheWireStaysSmall()
    {
        // Not a correctness requirement, but the reason we do not escape non-ASCII:
        // a Chinese room name would otherwise cost 6 bytes per character.
        string ours = RelayCodec.Serialize(new RoomCreatedMessage("房间一号"));
        string escaped = JsonSerializer.Serialize(new { gameCode = "房间一号", type = "ROOM_CREATED" });

        Assert.True(Encoding.UTF8.GetByteCount(ours) < Encoding.UTF8.GetByteCount(escaped));
        Assert.Contains("房间一号", ours);
    }

    // ── Round trip: every message a receiver actually parses ─────────────────

    [Fact]
    public void ReadableMessages_RoundTrip()
    {
        AssertRoundTrip(new RegisterRequest("tok", "game.id-1_x", "玩家一"));
        AssertRoundTrip(new CreateRoomRequest("房间", 8, true));
        AssertRoundTrip(new CreateRoomRequest());
        AssertRoundTrip(new JoinRoomRequest("ABC123"));
        AssertRoundTrip(new KickRequest("Bob"));
        AssertRoundTrip(new RegisterFailedMessage("bad_token"));
        AssertRoundTrip(new RoomCreatedMessage("ABC123"));
        AssertRoundTrip(new JoinSuccessMessage("ABC123", "Host", new[] { "Alice" }));
        AssertRoundTrip(new JoinFailedMessage("room_full"));
        AssertRoundTrip(new PlayerJoinedMessage("Alice", new[] { "Host", "Alice" }));
        AssertRoundTrip(new PlayerLeftMessage("Alice", new[] { "Host" }));
        AssertRoundTrip(new PlayerDisconnectedMessage("Alice", new[] { "Host", "Alice" }));
        AssertRoundTrip(new PlayerReconnectedMessage("Alice", new[] { "Host", "Alice" }));
        AssertRoundTrip(new HostChangedMessage("Alice", new[] { "Alice", "Host" }));
        AssertRoundTrip(new RoomListMessage(
            RelayMessageType.RoomList,
            new[] { new RoomInfo("ABC123", "房间", 2, 4, true, "Host") }));
    }

    [Fact]
    public void TryReadType_ReadsTheDispatchField()
    {
        foreach (object message in AllMessages())
        {
            string json = RelayCodec.Serialize(message);
            Assert.True(RelayCodec.TryReadType(json, out string type));
            Assert.NotEmpty(type);
        }
    }

    // ── Robustness: the relay parses hostile input from the open internet ─────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\":}")]
    [InlineData("{\"a\" \"b\"}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,2,3]")]
    [InlineData("\"bare string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"a\":\"unterminated")]
    [InlineData("{\"a\":\"bad escape \\q\"}")]
    [InlineData("{\"a\":\"short unicode \\u12\"}")]
    [InlineData("{\"a\":\"raw control \u0001\"}")]
    [InlineData("{} trailing")]
    public void MalformedInput_IsRejectedWithoutThrowing(string json)
    {
        Assert.False(RelayCodec.TryReadType(json, out _));
        Assert.False(RelayCodec.TryDeserialize(json, out RegisterRequest _));
    }

    [Fact]
    public void DeeplyNestedInput_IsRejectedInsteadOfBlowingTheStack()
    {
        string bomb = new string('[', 10_000) + new string(']', 10_000);
        Assert.False(RelayCodec.TryReadType(bomb, out _));
        Assert.False(RelayCodec.TryDeserialize("{\"a\":" + bomb + "}", out RegisterRequest _));
    }

    [Fact]
    public void MissingOrWrongTypedFields_FallBackToDefaults()
    {
        // A peer on an older protocol version must not crash the receiver.
        Assert.True(RelayCodec.TryDeserialize("{\"type\":\"REGISTER\"}", out RegisterRequest reg));
        Assert.Equal("", reg.Token);
        Assert.Equal("", reg.GameId);

        Assert.True(RelayCodec.TryDeserialize(
            "{\"roomName\":123,\"maxPlayers\":\"four\",\"type\":\"CREATE_ROOM\"}",
            out CreateRoomRequest create));
        Assert.Null(create.RoomName);
        Assert.Null(create.MaxPlayers);

        Assert.True(RelayCodec.TryDeserialize("{\"type\":\"JOIN_SUCCESS\"}", out JoinSuccessMessage join));
        Assert.Empty(join.Players);
    }

    [Fact]
    public void UnregisteredMessageType_FailsLoudlyOnWriteAndQuietlyOnRead()
    {
        // Forgetting to register a new message must be caught, not silently shipped.
        Assert.Throws<System.ArgumentException>(() => RelayCodec.Serialize(new { nope = 1 }));
        Assert.False(RelayCodec.TryDeserialize("{\"type\":\"X\"}", out ListRoomsRequest _));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertRoundTrip<T>(T message)
    {
        string json = RelayCodec.Serialize(message);
        Assert.True(RelayCodec.TryDeserialize(json, out T back), $"无法读回 {typeof(T).Name}: {json}");

        // Compared by re-serializing rather than with record equality: a record's generated
        // Equals compares array members by reference, so two structurally identical
        // messages would never match.
        Assert.Equal(json, RelayCodec.Serialize(back));
    }

    private static int CountProperties(JsonElement element)
    {
        int n = 0;
        foreach (JsonProperty _ in element.EnumerateObject()) n++;
        return n;
    }

    private static IEnumerable<object> AllMessages() => new object[]
    {
        new RegisterRequest("tok", "game", "Alice"),
        new ListRoomsRequest(),
        new CreateRoomRequest("房间", 4, true),
        new CreateRoomRequest(),
        new JoinRoomRequest("ABC123"),
        new LeaveRoomRequest(),
        new KickRequest("Bob"),
        new StartGameRequest(),
        new RegisterSuccessMessage(),
        new RegisterFailedMessage("bad_token"),
        new RoomListMessage(RelayMessageType.RoomList, new[] { new RoomInfo("A", "N", 1, 4, false, "H") }),
        new RoomCreatedMessage("ABC123"),
        new JoinSuccessMessage("ABC123", "Host", new[] { "Alice" }),
        new JoinFailedMessage("room_full"),
        new PlayerJoinedMessage("Alice", new[] { "Host", "Alice" }),
        new PlayerLeftMessage("Alice", new[] { "Host" }),
        new PlayerDisconnectedMessage("Alice", new[] { "Host", "Alice" }),
        new PlayerReconnectedMessage("Alice", new[] { "Host", "Alice" }),
        new HostChangedMessage("Alice", new[] { "Alice" }),
        new GameStartedMessage(),
        new LeaveSuccessMessage(),
    };
}
