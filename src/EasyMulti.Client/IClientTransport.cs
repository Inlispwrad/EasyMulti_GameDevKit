#nullable enable

using System;
using EasyMultiNet.Protocol;

namespace EasyMultiNet
{
    /// <summary>
    /// The client-side transport. <see cref="RelaySession"/> depends only on this
    /// interface, so swapping WebSocket for UDP (or a future Godot WebSocketPeer) never
    /// touches the state machine.
    /// <para>
    /// <b>Contract:</b> all three events fire on the caller's thread, inside
    /// <see cref="Poll"/>. The receive I/O may run on background tasks, but it must queue
    /// and be drained by <see cref="Poll"/>, so the client stays single-threaded and lock-free.
    /// </para>
    /// </summary>
    public interface IClientTransport : IDisposable
    {
        /// <summary>Connection established. The client replies with REGISTER.</summary>
        event Action? Opened;

        /// <summary>Connection failed or dropped; the argument is a human-readable reason.</summary>
        event Action<string>? Closed;

        /// <summary>A complete control message arrived (wire JSON + how it was delivered).</summary>
        event Action<string, DeliveryMode>? Received;

        /// <summary>A complete game-data frame arrived（[路由头+payload]，见 <see cref="Protocol.GameDataFraming"/>）.</summary>
        event Action<byte[], DeliveryMode>? ReceivedBinary;

        /// <summary>
        /// Begin connecting asynchronously; success/failure is reported via Opened/Closed.
        /// <para>
        /// 凭证随连接请求一起发出去（WS 走 <c>Sec-WebSocket-Protocol</c>，UDP 走 HELLO 帧），
        /// 中继验完才算连上 —— 没有「连上了再注册」这一步。验不过的话 <see cref="Closed"/>
        /// 会带着理由回来（bad_token / bad_game_id / …）。
        /// </para>
        /// </summary>
        void Connect(string host, int port, RegisterRequest credentials);

        /// <summary>Drain queued events and drive background bookkeeping. Call every 10–20 ms.</summary>
        void Poll();

        /// <summary>Send one control message. Order-preserving and non-blocking.</summary>
        void Send(string json, DeliveryMode mode);

        /// <summary>Send one game-data frame（对局数据，中继盲转）. Order-preserving and non-blocking.</summary>
        void SendBinary(byte[] frame, DeliveryMode mode);
    }
}
