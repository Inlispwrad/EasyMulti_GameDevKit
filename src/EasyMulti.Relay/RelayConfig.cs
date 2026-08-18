#nullable enable

using System.Text.Json;

namespace EasyMultiNet.Relay;

public sealed class RelayConfig
{
    public string Token { get; set; } = "";
    public bool WebSocketEnabled { get; set; } = true;
    public int WebSocketPort { get; set; } = 7777;
    public bool UdpEnabled { get; set; } = true;
    public int UdpPort { get; set; } = 7777;
    public int MaxConnections { get; set; } = 1000;
    public long IdleTimeoutMs { get; set; } = 60_000;
    public string LogLevel { get; set; } = "info";

    /// <summary>
    /// Build a config from (in increasing precedence): defaults → JSON config file →
    /// environment variables → command line. A non-empty token is required.
    /// </summary>
    public static RelayConfig Load(string[] args)
    {
        var config = new RelayConfig();

        // 1. JSON config file (--config path or ./easyrelay.config.json if present).
        string configPath = ArgValue(args, "--config") ?? "easyrelay.config.json";
        if (File.Exists(configPath))
        {
            try
            {
                var json = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(configPath));
                if (json != null)
                {
                    config.Token = json.Token ?? config.Token;
                    config.WebSocketEnabled = json.WebSocket?.Enabled ?? config.WebSocketEnabled;
                    config.WebSocketPort = json.WebSocket?.Port ?? config.WebSocketPort;
                    config.UdpEnabled = json.Udp?.Enabled ?? config.UdpEnabled;
                    config.UdpPort = json.Udp?.Port ?? config.UdpPort;
                    config.MaxConnections = json.MaxConnections ?? config.MaxConnections;
                    config.IdleTimeoutMs = json.IdleTimeoutMs ?? config.IdleTimeoutMs;
                    config.LogLevel = json.LogLevel ?? config.LogLevel;
                }
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"读配置 {configPath} 失败：{e.Message}");
            }
        }

        // 2. Environment variables.
        config.Token = Env("EASYMULTI_TOKEN") ?? config.Token;
        config.WebSocketPort = EnvInt("EASYMULTI_WS_PORT") ?? config.WebSocketPort;
        config.UdpPort = EnvInt("EASYMULTI_UDP_PORT") ?? config.UdpPort;
        config.MaxConnections = EnvInt("EASYMULTI_MAX_CONNECTIONS") ?? config.MaxConnections;
        config.IdleTimeoutMs = EnvInt("EASYMULTI_IDLE_TIMEOUT_MS") ?? config.IdleTimeoutMs;
        if (Env("EASYMULTI_WS_ENABLED") is string ws) config.WebSocketEnabled = IsTruthy(ws);
        if (Env("EASYMULTI_UDP_ENABLED") is string udp) config.UdpEnabled = IsTruthy(udp);

        // 3. Command line.
        config.Token = ArgValue(args, "--token") ?? config.Token;
        if (int.TryParse(ArgValue(args, "--port"), out int both)) // one port for both transports
        {
            config.WebSocketPort = both;
            config.UdpPort = both;
        }
        if (int.TryParse(ArgValue(args, "--ws-port"), out int wsPort)) config.WebSocketPort = wsPort;
        if (int.TryParse(ArgValue(args, "--udp-port"), out int udpPort)) config.UdpPort = udpPort;
        if (HasFlag(args, "--no-ws")) config.WebSocketEnabled = false;
        if (HasFlag(args, "--no-udp")) config.UdpEnabled = false;
        if (int.TryParse(ArgValue(args, "--max-connections"), out int maxConn)) config.MaxConnections = maxConn;
        if (ArgValue(args, "--log-level") is string level) config.LogLevel = level;

        if (!config.WebSocketEnabled && !config.UdpEnabled)
        {
            throw new InvalidOperationException("至少启用一种传输（WebSocket 或 UDP）");
        }

        if (string.IsNullOrWhiteSpace(config.Token))
        {
            throw new InvalidOperationException(
                "缺少 token：请通过 easyrelay.config.json 的 token、环境变量 EASYMULTI_TOKEN 或 --token 提供。"
                + "所有客户端必须携带同一个 token 才能连接。");
        }

        return config;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }

        return null;
    }

    private static bool HasFlag(string[] args, string flag) => args.Contains(flag);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static int? EnvInt(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : null;

    private static bool IsTruthy(string value) =>
        value is "1" or "true" or "TRUE" or "yes" or "YES" or "on" or "ON";

    // JSON config file shape.
    private sealed class ConfigFile
    {
        public string? Token { get; set; }
        public WebSocketSection? WebSocket { get; set; }
        public UdpSection? Udp { get; set; }
        public int? MaxConnections { get; set; }
        public long? IdleTimeoutMs { get; set; }
        public string? LogLevel { get; set; }
    }

    private sealed class WebSocketSection
    {
        public bool? Enabled { get; set; }
        public int? Port { get; set; }
    }

    private sealed class UdpSection
    {
        public bool? Enabled { get; set; }
        public int? Port { get; set; }
    }
}
