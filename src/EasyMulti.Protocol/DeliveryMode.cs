namespace EasyMultiNet.Protocol
{
    /// <summary>
    /// How a message is delivered over the wire.
    /// <para>
    /// WebSocket is TCP-backed, so it is always reliable+ordered regardless of this flag —
    /// an <see cref="Unreliable"/> message sent over WebSocket simply becomes reliable.
    /// UDP honors the flag: <see cref="Reliable"/> uses an ARQ channel (ack + retransmit,
    /// in-order delivery) for control traffic, while <see cref="Unreliable"/> is a
    /// best-effort datagram with no retransmission — the right choice for high-frequency
    /// game state where a fresher packet supersedes a lost one.
    /// </para>
    /// </summary>
    public enum DeliveryMode : byte
    {
        /// <summary>Guaranteed delivery, in order. Used for control messages.</summary>
        Reliable = 0,

        /// <summary>Best effort. Used for high-frequency game state over UDP.</summary>
        Unreliable = 1,
    }
}
