#nullable enable

using System.Buffers.Binary;

namespace EasyMulti.Protocol;

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
}

public readonly record struct FrameHeader(
    FrameFlags Flags,
    uint Seq,
    uint Ack,
    uint AckBitfield,
    ushort FragIndex,
    ushort FragCount);

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
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], header.Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], header.Ack);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[12..], header.AckBitfield);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[16..], header.FragIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[18..], header.FragCount);
        payload.CopyTo(dst[HeaderSize..]);
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
        // ACK_ONLY frames must not also claim reliability or fragmentation.
        if ((flags & FrameFlags.AckOnly) != 0 && (flags & ~FrameFlags.AckOnly) != 0) return false;

        header = new FrameHeader(
            flags,
            BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(src[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(src[18..]));

        payload = src[HeaderSize..];
        return true;
    }
}
