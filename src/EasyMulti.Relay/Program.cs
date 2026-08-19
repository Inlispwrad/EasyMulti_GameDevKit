#nullable enable

using EasyMultiNet.Relay;

// ── EasyMulti relay ──────────────────────────────────────────────────────────
// A self-hostable relay for small multiplayer games. It only forwards data; game
// logic lives in the host (client-hosted or dedicated). Accepts WebSocket and UDP,
// routes rooms by gameId, and gates everything behind a shared token (anti-crawler,
// not a real security boundary). See docs/PROTOCOL.md and docs/DEPLOY.md.

return Run(args);

static int Run(string[] args)
{
    // The logs are Chinese; without this a Windows console renders them as '?'.
    try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
    catch (System.IO.IOException) { /* output is redirected — nothing to set */ }

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

    Console.WriteLine($"[EasyMulti] Starting: token={(config.Token.Length > 0 ? "configured" : "MISSING")}");
    Console.WriteLine($"[EasyMulti] WebSocket: {(config.WebSocketEnabled ? $"port {config.WebSocketPort}" : "disabled")}");
    Console.WriteLine($"[EasyMulti] UDP:       {(config.UdpEnabled ? $"port {config.UdpPort}" : "disabled")}");

    var server = new RelayServer(config);

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        server.Stop();
    };

    server.Run();
    Console.WriteLine("[EasyMulti] Stopped");
    return 0;
}
