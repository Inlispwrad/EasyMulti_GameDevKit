#nullable enable

using System;

namespace EasyMultiNet
{
    /// <summary>
    /// Connection identity for a session. The token is the relay's shared secret
    /// (anti-crawler, not a hard security boundary), the gameId scopes rooms into a
    /// namespace, and the playerId is unique within that game.
    /// </summary>
    public sealed class SessionConfig
    {
        public SessionConfig(string token, string gameId, string playerId)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token 不能为空", nameof(token));
            if (string.IsNullOrWhiteSpace(gameId)) throw new ArgumentException("gameId 不能为空", nameof(gameId));
            if (string.IsNullOrWhiteSpace(playerId)) throw new ArgumentException("playerId 不能为空", nameof(playerId));

            Token = token;
            GameId = gameId;
            PlayerId = playerId;
        }

        public string Token { get; }
        public string GameId { get; }
        public string PlayerId { get; }
    }
}
