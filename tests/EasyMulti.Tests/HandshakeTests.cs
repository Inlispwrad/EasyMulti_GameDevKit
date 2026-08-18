#nullable enable

using EasyMultiNet.Protocol;
using Xunit;

namespace EasyMultiNet.Tests;

/// <summary>
/// 连接凭证的搬运格式。凭证随连接请求一起到（WS 走 <c>Sec-WebSocket-Protocol</c>），
/// 所以这个编码是 C# 与浏览器 JS 之间的一道缝 —— 两边任何一侧改了都会静默连不上。
/// </summary>
public class HandshakeTests
{
    [Fact]
    public void RoundTrips()
    {
        var credentials = new RegisterRequest("demo-token", "my-game", "Alice");
        string protocol = RelayHandshake.Encode(credentials);

        Assert.StartsWith(RelayHandshake.CredentialPrefix, protocol);
        Assert.True(RelayHandshake.TryDecode(new[] { RelayHandshake.Protocol, protocol }, out RegisterRequest back));
        Assert.Equal("demo-token", back.Token);
        Assert.Equal("my-game", back.GameId);
        Assert.Equal("Alice", back.PlayerId);
    }

    /// <summary>
    /// playerId 允许中文和空格 —— 直接当子协议名是非法的（RFC 6455 的 token 字符集），
    /// 所以整包 base64url。这条要是挂了，说明编码退化成了「大部分名字能用」。
    /// </summary>
    [Fact]
    public void SurvivesNonAsciiPlayerId()
    {
        string protocol = RelayHandshake.Encode(new RegisterRequest("t", "g", "小明 Jin"));

        Assert.DoesNotContain(" ", protocol);
        Assert.True(RelayHandshake.TryDecode(new[] { protocol }, out RegisterRequest back));
        Assert.Equal("小明 Jin", back.PlayerId);
    }

    /// <summary>
    /// 浏览器侧 easymulti.js 的 credentialProtocol() 真实产出，钉死在这里。
    /// 改了 C# 的编码而没同步 JS（或反过来），这条会红。
    /// </summary>
    [Fact]
    public void AcceptsWhatTheBrowserSdkProduces()
    {
        const string fromJs =
            "em.eyJ0b2tlbiI6ImRlbW8tdG9rZW4iLCJnYW1lSWQiOiJteS1nYW1lIiwicGxheWVySWQiOiLlsI_mmI4gSmluIiwidHlwZSI6IlJFR0lTVEVSIn0";

        Assert.True(RelayHandshake.TryDecode(new[] { "easymulti", fromJs }, out RegisterRequest back));
        Assert.Equal("demo-token", back.Token);
        Assert.Equal("my-game", back.GameId);
        Assert.Equal("小明 Jin", back.PlayerId);

        // 反向也得一致：同一份凭证，C# 编出来的和浏览器编出来的是同一个串。
        Assert.Equal(fromJs, RelayHandshake.Encode(new RegisterRequest("demo-token", "my-game", "小明 Jin")));
    }

    [Theory]
    [InlineData("easymulti")]        // 只有固定子协议名，没带凭证
    [InlineData("em.!!!not-base64")] // 前缀对了但解不开
    [InlineData("em.")]              // 空凭证
    public void RefusesGarbage(string protocol)
    {
        Assert.False(RelayHandshake.TryDecode(new[] { protocol }, out _));
    }

    [Fact]
    public void RefusesNothingAtAll()
    {
        Assert.False(RelayHandshake.TryDecode(null, out _));
        Assert.False(RelayHandshake.TryDecode(new string[0], out _));
    }
}
