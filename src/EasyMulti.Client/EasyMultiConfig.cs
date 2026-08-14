#nullable enable

namespace EasyMulti.Client;

/// <summary>
/// Connection identity for a client or host. The token is the relay's shared secret
/// (anti-crawler, not a hard security boundary), the gameId scopes rooms into a
/// namespace, and the playerName is unique within that game.
/// </summary>
public sealed class EasyMultiConfig
{
    public EasyMultiConfig(string token, string gameId, string playerName)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token 不能为空", nameof(token));
        if (string.IsNullOrWhiteSpace(gameId)) throw new ArgumentException("gameId 不能为空", nameof(gameId));
        if (string.IsNullOrWhiteSpace(playerName)) throw new ArgumentException("playerName 不能为空", nameof(playerName));

        Token = token;
        GameId = gameId;
        PlayerName = playerName;
    }

    public string Token { get; }
    public string GameId { get; }
    public string PlayerName { get; }
}
