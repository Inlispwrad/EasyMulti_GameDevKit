#nullable enable

using EasyMultiNet;
using MemoryPack;

// ─────────────────────────────────────────────────────────────────────────────
// 对局消息：T 就是消息通道。两端共用这一份定义，Send<SayMsg> 出、Receive<SayMsg> 进，
// SDK 的默认壳按类型路由 —— 没有手写的编码格式，也没有分发 switch。
// 序号由 host 的 Core 统一发放，所以所有人看到的顺序完全一致。
//
// 这份定义和 samples/ChatGodot/ChatMessages.cs 是同一套，两个样例可以互通。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一条定序过的发言。</summary>
[MemoryPackable]
public partial record SayMsg(int Seq, string Who, string Text);

/// <summary>当前玩家名单（host 不是玩家，不在其中）。</summary>
[MemoryPackable]
public partial record WhoMsg(int Seq, string[] Players);

/// <summary>
/// 对局数据的编解码器。SDK 是零依赖的，所以这 8 行住在你的工程里 ——
/// 填进配置的 <c>Codec = new MemoryPackCodec()</c> 就是官方推荐路径。
/// </summary>
public sealed class MemoryPackCodec : IPayloadCodec
{
    public byte[] Encode<T>(T value) => MemoryPackSerializer.Serialize(value);

    public T Decode<T>(ReadOnlySpan<byte> body) => MemoryPackSerializer.Deserialize<T>(body)!;
}
