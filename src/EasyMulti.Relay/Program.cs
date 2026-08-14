#nullable enable

using EasyMulti.Relay;

// ── EasyMulti relay ──────────────────────────────────────────────────────────
// A self-hostable relay for small multiplayer games. It only forwards data; game
// logic lives in the host (client-hosted or dedicated). Accepts WebSocket and UDP,
// routes rooms by gameId, and gates everything behind a shared token (anti-crawler,
// not a real security boundary). See docs/PROTOCOL.md and docs/DEPLOY.md.

return Run(args);

static int Run(string[] args)
{
    RelayConfig config;
    try
    {
        config = RelayConfig.Load(args);
    }
    catch (InvalidOperationException e)
    {
        Console.Error.WriteLine("[EasyMulti] " + e.Message);
        return 1;
    }

    Console.WriteLine($"[EasyMulti] 启动：token={(config.Token.Length > 0 ? "已配置" : "缺失")}");
    Console.WriteLine($"[EasyMulti] WebSocket: {(config.WebSocketEnabled ? $"端口 {config.WebSocketPort}" : "关闭")}");
    Console.WriteLine($"[EasyMulti] UDP:        {(config.UdpEnabled ? $"端口 {config.UdpPort}" : "关闭")}");

    var server = new RelayServer(config);

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        server.Stop();
    };

    server.Run();
    Console.WriteLine("[EasyMulti] 已停止");
    return 0;
}
