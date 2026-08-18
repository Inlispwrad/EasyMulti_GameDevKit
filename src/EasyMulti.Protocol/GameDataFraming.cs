#nullable enable

using System;
using System.Buffers.Binary;
using System.Text;

namespace EasyMultiNet.Protocol
{
    /// <summary>
    /// 对局数据帧的路由头：<c>[2B 小端 id 长度][id UTF8 字节][payload 原始字节]</c>。
    /// <para>
    /// 这就是「给中继看的那层皮」：发送方向 id＝收件人（空串＝广播），转发方向 id＝发件人。
    /// 皮之外的 payload 是黑盒 —— 中继一个字节不解析，MemoryPack / 任意二进制 / UTF8 文本
    /// 都原样直通，没有 base64、没有 JSON 转义。
    /// </para>
    /// </summary>
    public static class GameDataFraming
    {
        /// <summary>头部定长部分（id 长度前缀）。</summary>
        public const int HeaderSize = 2;

        /// <summary>拼一帧：<paramref name="id"/> 发送时是收件人（空串＝广播），转发时是发件人。</summary>
        public static byte[] Encode(string id, byte[] payload)
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(id);
            if (idBytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException("路由 id 过长", nameof(id));
            }

            var frame = new byte[HeaderSize + idBytes.Length + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)idBytes.Length);
            idBytes.CopyTo(frame, HeaderSize);
            payload.CopyTo(frame, HeaderSize + idBytes.Length);
            return frame;
        }

        /// <summary>拆一帧。畸形（太短 / 长度越界）返回 false。<paramref name="payload"/> 是独立拷贝。</summary>
        public static bool TryDecode(byte[] frame, out string id, out byte[] payload)
        {
            id = "";
            payload = Array.Empty<byte>();
            if (frame.Length < HeaderSize) return false;

            int idLength = BinaryPrimitives.ReadUInt16LittleEndian(frame);
            if (HeaderSize + idLength > frame.Length) return false;

            id = Encoding.UTF8.GetString(frame, HeaderSize, idLength);
            int payloadLength = frame.Length - HeaderSize - idLength;
            if (payloadLength > 0)
            {
                payload = new byte[payloadLength];
                Buffer.BlockCopy(frame, HeaderSize + idLength, payload, 0, payloadLength);
            }

            return true;
        }
    }
}
