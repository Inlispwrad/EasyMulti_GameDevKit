#nullable enable

using System;
using System.Buffers.Binary;

namespace EasyMultiNet.Protocol
{
    // Binary UDP framing shared by relay and client so both sides speak the exact same
    // wire format. Everything is little-endian. A frame carries a *message* (the unit the
    // relay forwards); large messages are split into fragments that all share one sequence
    // number and are reassembled before delivery.

    /// <summary>Frame flags. A frame is reliable iff <see cref="Reliable"/> is set.</summary>
    [Flags]
    public enum FrameFlags : byte
    {
        None = 0,

        /// <summary>Reliable (ack + retransmit, in-order delivery). Absence = best-effort.</summary>
        Reliable = 1,

        /// <summary>This frame is the first fragment of a message.</summary>
        FragFirst = 2,

        /// <summary>This frame is the last fragment of a message.</summary>
        FragLast = 4,

        /// <summary>Carries no payload; its only job is to carry an ack.</summary>
        AckOnly = 8,

        /// <summary>
        /// Best-effort goodbye: the sender is closing this peer for good. Lets the other
        /// side free the connection immediately instead of waiting out its idle timeout
        /// (lost Bye = fall back to that timeout). Never retransmitted.
        /// </summary>
        Bye = 16,

        /// <summary>
        /// 对局数据帧：payload 不是控制 JSON，而是 <c>[2B 路由 id 长度][id UTF8][原始字节]</c>
        /// （见 <see cref="GameDataFraming"/>）。中继只读路由头，原始字节一个都不解析 ——
        /// MemoryPack 等二进制编码零膨胀直通。可与 Reliable / 分片位组合。
        /// </summary>
        GameData = 32,

        /// <summary>
        /// 连接请求：payload 是凭证 JSON（<c>REGISTER</c> 的三件套 token/gameId/playerId）。
        /// <para>
        /// UDP 没有握手，这一位就是握手 —— 陌生地址发来的**第一个**数据报必须带它，中继在
        /// 建立任何状态**之前**验凭证：不合格就丢掉，一个字节都不分配。必须与
        /// <see cref="Reliable"/> 同用（走正常 ARQ，丢了会重传）；对端 ack 它但不向上交付。
        /// </para>
        /// </summary>
        Hello = 64,
    }

    /// <summary>
    /// One frame's header fields. Deliberately a plain <c>readonly struct</c> rather than a
    /// record: this is constructed for every datagram on the hot path, so it must stay
    /// allocation-free. It never travels through JSON and no consumer of the SDK sees it.
    /// </summary>
    public readonly struct FrameHeader
    {
        public FrameHeader(
            FrameFlags flags,
            uint seq,
            uint ack,
            uint ackBitfield,
            ushort fragIndex,
            ushort fragCount)
        {
            Flags = flags;
            Seq = seq;
            Ack = ack;
            AckBitfield = ackBitfield;
            FragIndex = fragIndex;
            FragCount = fragCount;
        }

        public FrameFlags Flags { get; }
        public uint Seq { get; }
        public uint Ack { get; }
        public uint AckBitfield { get; }
        public ushort FragIndex { get; }
        public ushort FragCount { get; }
    }

    public static class UdpFrame
    {
        /// <summary>Magic byte that starts every EasyMulti UDP frame.</summary>
        public const byte Magic = 0xE9;

        public const byte Version = 0x01;

        /// <summary>Header size in bytes.</summary>
        public const int HeaderSize = 20;

        /// <summary>
        /// Payload budget per datagram. Kept comfortably below the common 1500-byte MTU so
        /// fragmented frames survive typical NATs / VPNs without IP fragmentation.
        /// </summary>
        public const int DefaultMtu = 1200;

        public const int MaxPayload = DefaultMtu - HeaderSize;

        /// <summary>Encode a frame into <paramref name="dst"/>. Returns the number of bytes written.</summary>
        public static int Write(Span<byte> dst, FrameHeader header, ReadOnlySpan<byte> payload)
        {
            dst[0] = Magic;
            dst[1] = Version;
            dst[2] = (byte)header.Flags;
            dst[3] = 0; // reserved
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4), header.Seq);
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8), header.Ack);
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(12), header.AckBitfield);
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(16), header.FragIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(18), header.FragCount);
            payload.CopyTo(dst.Slice(HeaderSize));
            return HeaderSize + payload.Length;
        }

        /// <summary>
        /// Parse a frame. Returns false if the buffer is too short, has a bad magic/version,
        /// or is otherwise malformed. <paramref name="payload"/> points into <paramref name="src"/>.
        /// </summary>
        public static bool TryRead(ReadOnlySpan<byte> src, out FrameHeader header, out ReadOnlySpan<byte> payload)
        {
            header = default;
            payload = default;
            if (src.Length < HeaderSize) return false;
            if (src[0] != Magic) return false;
            if (src[1] != Version) return false;

            var flags = (FrameFlags)src[2];
            // ACK_ONLY / BYE frames must not also claim reliability or fragmentation.
            if ((flags & FrameFlags.AckOnly) != 0 && (flags & ~FrameFlags.AckOnly) != 0) return false;
            if ((flags & FrameFlags.Bye) != 0 && (flags & ~FrameFlags.Bye) != 0) return false;
            // HELLO 必须走可靠通道：它是连接请求，丢了得重传。
            if ((flags & FrameFlags.Hello) != 0 && (flags & FrameFlags.Reliable) == 0) return false;

            header = new FrameHeader(
                flags,
                BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12)),
                BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(16)),
                BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(18)));

            payload = src.Slice(HeaderSize);
            return true;
        }
    }
}
