#nullable enable

using System;
using System.Collections.Generic;

namespace EasyMultiNet
{
    /// <summary>
    /// 对局数据 body 的编解码器。默认推荐 MemoryPack —— 适配器就 8 行，但它必须住在
    /// <b>你的游戏程序集</b>里（`[MemoryPackable]` 类型放进 netstandard2.1 程序集会在
    /// net8.0 宿主上 TypeLoadException，且 SDK 零第三方依赖）：
    /// <code>
    /// public sealed class MemoryPackCodec : IPayloadCodec
    /// {
    ///     public byte[] Encode&lt;T&gt;(T value) => MemoryPackSerializer.Serialize(value);
    ///     public T Decode&lt;T&gt;(ReadOnlySpan&lt;byte&gt; body) => MemoryPackSerializer.Deserialize&lt;T&gt;(body)!;
    /// }
    /// </code>
    /// 挂上：<c>EasyMulti.Init(new() { …, Codec = new MemoryPackCodec() });</c>。
    /// 想换 protobuf / 自定义二进制，替换这个接口即可；想连「类型路由壳」都自己定义，
    /// 用低层 <see cref="RelaySession.SendGameData"/> 收发裸字节。
    /// </summary>
    public interface IPayloadCodec
    {
        byte[] Encode<T>(T value);

        T Decode<T>(ReadOnlySpan<byte> body);
    }

    /// <summary>
    /// 默认壳：<c>Send&lt;T&gt;</c> 发出的 payload＝<c>[4B 小端类型键][codec 编码的 body]</c>，
    /// 类型键＝FNV-1a(类型 FullName)。<b>T 本身就是消息通道</b>：接收端 <c>Receive&lt;T&gt;</c>
    /// 按类型注册，收到谁的键就调谁的 handler，没注册的类型静默丢弃。
    /// 两端共用同一套消息类型定义（同名同命名空间），键天然一致；改类型名＝改协议。
    /// </summary>
    internal sealed class PayloadRouter
    {
        private readonly Dictionary<uint, (Type Type, Action<string, byte[]> Invoke)> _routes =
            new Dictionary<uint, (Type Type, Action<string, byte[]> Invoke)>();

        /// <summary>注册一条类型通道。同一 T 多次注册＝叠加（都会被调）。</summary>
        public void Register<T>(Action<string, T> handler)
        {
            uint key = KeyOf(typeof(T));
            void Invoke(string from, byte[] payload) =>
                handler(from, RequireCodec().Decode<T>(payload.AsSpan(4)));

            if (_routes.TryGetValue(key, out (Type Type, Action<string, byte[]> Invoke) existing))
            {
                if (existing.Type != typeof(T))
                {
                    throw new InvalidOperationException(
                        $"类型名哈希冲突：{existing.Type.FullName} 与 {typeof(T).FullName} 是两个不同的类型，"
                        + "但它们类型名的 FNV-1a 哈希值恰好相同（4 字节类型键一致，SDK 无法在线上区分二者）。"
                        + "给其中一个改名即可。");
                }

                _routes[key] = (typeof(T), existing.Invoke + Invoke);
            }
            else
            {
                _routes[key] = (typeof(T), Invoke);
            }
        }

        /// <summary>按 payload 前 4 字节的类型键分发。没注册的类型不调任何人。</summary>
        public void Dispatch(string from, byte[] payload)
        {
            if (payload.Length < 4) return;

            uint key = (uint)(payload[0] | payload[1] << 8 | payload[2] << 16 | payload[3] << 24);
            if (_routes.TryGetValue(key, out (Type Type, Action<string, byte[]> Invoke) route))
            {
                route.Invoke(from, payload);
            }
        }

        /// <summary>拼一条 <c>[4B 类型键][body]</c>。</summary>
        public static byte[] EncodeMessage<T>(T value)
        {
            byte[] body = RequireCodec().Encode(value);
            uint key = KeyOf(typeof(T));
            var frame = new byte[4 + body.Length];
            frame[0] = (byte)key;
            frame[1] = (byte)(key >> 8);
            frame[2] = (byte)(key >> 16);
            frame[3] = (byte)(key >> 24);
            body.CopyTo(frame, 4);
            return frame;
        }

        /// <summary>FNV-1a(FullName)：稳定跨进程/跨平台（string.GetHashCode 是随机化的，不能用）。</summary>
        internal static uint KeyOf(Type type)
        {
            string name = type.FullName ?? type.Name;
            uint hash = 2166136261;
            foreach (char c in name)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash;
        }

        private static IPayloadCodec RequireCodec() =>
            EasyMulti.Codec ?? throw new InvalidOperationException(
                "先挂编解码器：EasyMulti.Init(new() { …, Codec = new MemoryPackCodec() });"
                + "（8 行参考实现见 IPayloadCodec 文档 / docs/USAGE.md）");
    }
}
