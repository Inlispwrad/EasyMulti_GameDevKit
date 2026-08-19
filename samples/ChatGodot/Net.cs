using EasyMultiNet;
using Godot;

/// <summary>
/// 全项目唯一一处和中继打交道的地方。游戏代码用 <c>EasyMulti.Client.Connect(名字)</c> /
/// <c>EasyMulti.Host.Open(名字, 房名, 人数)</c> 拿角色实例，只会碰到
/// <see cref="EasyMultiClient"/> / <see cref="EasyMultiHost"/> / <see cref="Room"/> 三个类型。
/// <para>
/// <b>这是个测试工具，所以地址和 token 由界面在运行时输入，代码里一个字都不写死。</b>
/// 换成你自己的游戏时，这些值通常来自你的构建配置 —— 但无论如何，
/// <b>别把 token 提交进仓库</b>。
/// </para>
/// </summary>
public partial class Net : Node
{
    /// <summary>上次填过的连接信息记在这儿。<c>user://</c> 在工程目录之外，不会被提交。</summary>
    private const string SettingsPath = "user://relay.cfg";

    /// <summary>界面填完点「连接」后调这里。之后才能 Connect / Open。</summary>
    public static void Configure(string relayHost, int relayPort, string token) =>
        EasyMulti.Init(new()
        {
            Token     = token,
            GameId    = "chat-godot",
            RelayHost = relayHost,
            RelayPort = relayPort,
            Codec     = new MemoryPackCodec(), // 默认壳：T 即消息通道，body 走 MemoryPack
        });

    /// <summary>把这次填的存起来，免得每次都重敲 64 位的 token。</summary>
    public static void Remember(string relayHost, int relayPort, string token, string playerName)
    {
        var cfg = new ConfigFile();
        cfg.SetValue("relay", "host", relayHost);
        cfg.SetValue("relay", "port", relayPort);
        cfg.SetValue("relay", "token", token);
        cfg.SetValue("relay", "name", playerName);
        cfg.Save(SettingsPath);
    }

    /// <summary>读回上次填的。没存过就给一套本地默认值。</summary>
    public static (string Host, int Port, string Token, string Name) Recall()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) != Error.Ok) return ("127.0.0.1", 7777, "", "");

        return (
            cfg.GetValue("relay", "host", "127.0.0.1").AsString(),
            cfg.GetValue("relay", "port", 7777).AsInt32(),
            cfg.GetValue("relay", "token", "").AsString(),
            cfg.GetValue("relay", "name", "").AsString());
    }

    /// <summary>每帧驱动所有连接；SDK 事件全部在这里、同一个线程上回调。</summary>
    public override void _Process(double delta) => EasyMulti.Poll();

    public override void _ExitTree() => EasyMulti.Shutdown();
}
