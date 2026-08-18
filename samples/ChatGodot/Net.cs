using EasyMultiNet;
using Godot;

/// <summary>
/// 全项目唯一一处和中继打交道的地方。部署完中继后你只改下面那份配置；
/// 游戏代码用 <c>EasyMulti.Client.Connect(名字)</c> / <c>EasyMulti.Host.Open(名字, 房名, 人数)</c>
/// 拿角色实例，只会碰到 <see cref="EasyMultiClient"/> / <see cref="EasyMultiHost"/> / <see cref="Room"/> 三个类型。
/// </summary>
public partial class Net : Node
{
    public override void _Ready() => EasyMulti.Init(new()
    {
        // ── 部署完中继后，要改的就是这四行 ───────────────────────────────
        Token     = "demo-token",
        GameId    = "chat-godot",
        RelayHost = "127.0.0.1",
        RelayPort = 7777,

        // 浏览器 / WASM 导出改成 Transport = EasyMultiTransport.Wss（HTTPS 页面只能连 wss）。
        Codec = new MemoryPackCodec(), // 默认壳：T 即消息通道，body 走 MemoryPack
    });

    /// <summary>每帧驱动所有连接；SDK 事件全部在这里、同一个线程上回调。</summary>
    public override void _Process(double delta) => EasyMulti.Poll();

    public override void _ExitTree() => EasyMulti.Shutdown();
}
