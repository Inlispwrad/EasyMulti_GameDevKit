using Xunit;

namespace EasyMultiNet.Tests;

/// <summary>
/// 中继地址的拼装规则（<c>EasyMultiTransport.Ws</c> / <c>Wss</c> + 反代路径）。
/// <para>
/// 这里只守拼装这一半。真 wss 的端到端在自动化测试里跑不起来：要一张真证书，而
/// netstandard2.1 的 <c>ClientWebSocketOptions</c> 没有跳过证书校验的口子（那是 .NET 5
/// 才加的 RemoteCertificateValidationCallback），自签也绕不过去。明文 ws 那条路径由
/// <see cref="FacadeTests"/> 对着真中继端到端覆盖。
/// </para>
/// </summary>
public class WebSocketUrlTests
{
    [Fact]
    public void Defaults_To_Plain_Ws()
    {
        System.Uri url = WebSocketClientTransport.BuildUrl("relay.example.com", 7777, secure: false, path: "/");

        Assert.Equal("ws", url.Scheme);
        Assert.Equal("relay.example.com", url.Host);
        Assert.Equal(7777, url.Port);
        Assert.Equal("/", url.AbsolutePath);
    }

    [Fact]
    public void Secure_Switches_Scheme()
    {
        System.Uri url = WebSocketClientTransport.BuildUrl("relay.example.com", 443, secure: true, path: "/");

        Assert.Equal("wss", url.Scheme);
        Assert.Equal(443, url.Port);
    }

    [Theory]
    [InlineData("/em")]
    [InlineData("em")] // 少写斜杠是常见笔误，补上而不是拼出 wss://host:443em
    public void Reverse_Proxy_Path_Is_Applied(string path)
    {
        System.Uri url = WebSocketClientTransport.BuildUrl("relay.example.com", 443, secure: true, path);

        Assert.Equal("wss", url.Scheme);
        Assert.Equal("relay.example.com", url.Host);
        Assert.Equal("/em", url.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_Path_Falls_Back_To_Root(string? path)
    {
        System.Uri url = WebSocketClientTransport.BuildUrl("127.0.0.1", 7777, secure: false, path!);

        Assert.Equal("/", url.AbsolutePath);
    }
}
