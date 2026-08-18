#nullable enable

using System.Net;
using EasyMultiNet.Protocol;
using Xunit;

namespace EasyMultiNet.Tests;

/// <summary>
/// The promise is "deploy the relay once and stop thinking about it". These cover the two
/// things that quietly break that: a platform health probe that gets a non-2xx answer, and
/// per-IP bookkeeping that grows without bound on a public endpoint.
/// </summary>
[Collection(RelayCollection.Name)]
public class RelayDeploymentTests
{
    private const string Token = "test-token";

    [Fact]
    public async Task HealthProbe_Gets200_SoPlatformsDoNotRestartTheRelay()
    {
        using var relay = new RelayHarness();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{relay.WsPort}/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NonUpgradeRequests_StillGet426()
    {
        using var relay = new RelayHarness();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{relay.WsPort}/");

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public void ManyBadTokens_NeverLockOutALegitimateClientFromTheSameAddress()
    {
        // Per-IP bad-token counting exists for logging and must stay that way: a developer
        // who ships a mistyped token would otherwise see "worked 20 times, then silence",
        // and everyone behind the same NAT would be collateral damage.
        using var relay = new RelayHarness();

        for (int attempt = 0; attempt < 30; attempt++)
        {
            var bad = RelaySession.CreateUdp(new SessionConfig("wrong-token", "test-game", $"Bad{attempt}"));
            string reason = "";
            bad.Rejected += r => reason = r;
            bad.Connect("127.0.0.1", relay.UdpPort);
            PumpUntil(bad, () => reason.Length > 0, 2000);

            Assert.Contains("bad_token", reason); // 每一次都是同样的答复，不会中途变脸
            bad.Dispose();
        }

        var good = RelaySession.CreateUdp(new SessionConfig(Token, "test-game", "Good"));
        good.Connect("127.0.0.1", relay.UdpPort);
        PumpUntil(good, () => good.State == SessionState.Lobby, 5000);

        Assert.Equal(SessionState.Lobby, good.State);
        good.Dispose();
    }

    private static void PumpUntil(RelaySession client, Func<bool> done, int timeoutMs)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && deadline.ElapsedMilliseconds < timeoutMs)
        {
            client.Poll();
            Thread.Sleep(5);
        }
    }
}
