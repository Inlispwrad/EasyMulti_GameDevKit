#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace EasyMultiNet.Protocol
{
    /// <summary>Tunables for a <see cref="UdpPeer"/>. Defaults suit a LAN/dev relay.</summary>
    public sealed class UdpPeerOptions
    {
        /// <summary>Maximum datagram size in bytes (header included).</summary>
        public int Mtu { get; set; } = UdpFrame.DefaultMtu;

        /// <summary>Initial retransmit timeout for reliable messages.</summary>
        public long BaseRtoMs { get; set; } = 200;

        /// <summary>Retransmit timeout upper bound (exponential backoff caps here).</summary>
        public long MaxRtoMs { get; set; } = 2000;

        /// <summary>Close the peer after this many milliseconds with no inbound traffic.</summary>
        public long IdleTimeoutMs { get; set; } = 60_000;

        /// <summary>Maximum un-acked reliable messages before the peer is considered broken.</summary>
        public int MaxPendingMessages { get; set; } = 1024;

        /// <summary>
        /// 隔多久发一次保活（毫秒）。**0 = 不主动发，只回应** —— 中继侧就该是 0。
        /// <para>
        /// 只有客户端需要发：要保住的是它那条 NAT 映射，而映射只能被出站包刷新。
        /// 取值要明显小于 <see cref="IdleTimeoutMs"/>，也要小于常见 NAT 的映射超时
        /// （真实设备大约 30 秒起，RFC 6263 给 UDP 定的下限是 15 秒）。
        /// </para>
        /// </summary>
        public long KeepAliveMs { get; set; }

        /// <summary>Min interval between standalone ACK frames when nothing else is being sent.</summary>
        public long AckIntervalMs { get; set; } = 10;

        public int MaxPayload => Mtu - UdpFrame.HeaderSize;
    }

    /// <summary>
    /// One UDP "connection": a best-effort datagram channel plus a reliable in-order channel
    /// (ack + retransmit + fragment reassembly) sharing a single socket endpoint.
    /// <para>
    /// Used identically by the relay (one peer per client endpoint) and the client (one peer
    /// to the relay). Thread-safe: <see cref="Send"/>, <see cref="HandleDatagram"/> and
    /// <see cref="Tick"/> may be called from different threads. Callbacks are invoked on the
    /// caller's thread and must be non-blocking (they typically just enqueue).
    /// </para>
    /// </summary>
    public sealed class UdpPeer
    {
        // Environment.TickCount64 is not part of netstandard2.1, so the monotonic clock
        // comes from a process-wide Stopwatch instead. Same semantics for our purposes:
        // monotonic, millisecond resolution, never wraps in any realistic uptime.
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly Action<byte[], int> _send;
        private readonly Action<byte[], DeliveryMode, bool> _deliver; // (payload, mode, isGameData)
        private readonly Action<string> _close;
        private readonly UdpPeerOptions _opts;
        private readonly object _gate = new object();
        private readonly byte[] _scratch;

        private uint _sendSeq = 1;          // next reliable message seq
        private uint _unreliableSeq;        // next best-effort message seq
        private uint _recvNext = 1;         // next expected reliable seq
        private uint _ack;                  // cumulative ack == _recvNext - 1
        private uint _ackBitfield;
        private bool _ackDirty;
        private long _lastSentMs;

        private readonly SortedDictionary<uint, Pending> _pending = new SortedDictionary<uint, Pending>();
        private readonly SortedDictionary<uint, Received> _incoming = new SortedDictionary<uint, Received>();

        /// <summary>HELLO 交付到这里然后丢掉 —— 它只需要被 ack，不需要给上层看。</summary>
        private readonly List<(byte[], DeliveryMode, bool)> _discard = new List<(byte[], DeliveryMode, bool)>();

        private long _rtoMs;
        private long _lastActivityMs;
        private bool _closed;

        public UdpPeer(
            string address,
            Action<byte[], int> send,
            Action<byte[], DeliveryMode, bool> deliver,
            Action<string> close,
            UdpPeerOptions? options = null)
        {
            Address = address;
            _send = send;
            _deliver = deliver;
            _close = close;
            _opts = options ?? new UdpPeerOptions();
            _scratch = new byte[_opts.Mtu];
            _rtoMs = _opts.BaseRtoMs;
            _lastActivityMs = NowMs();
        }

        /// <summary>Human-readable remote address (for logs).</summary>
        public string Address { get; }

        public long BytesSent { get; private set; }
        public long BytesReceived { get; private set; }
        public long MessagesReceived { get; private set; }
        public long MessagesSent { get; private set; }
        public bool IsClosed => _closed;

        /// <summary>Reliable messages sent but not yet acknowledged. Diagnostic + tests.</summary>
        public int PendingCount { get { lock (_gate) return _pending.Count; } }

        /// <summary>Reliable messages received out of order, waiting for the gap to fill. Diagnostic + tests.</summary>
        public int IncomingCount { get { lock (_gate) return _incoming.Count; } }

        /// <summary>Seconds since the last inbound datagram.</summary>
        public double IdleSeconds => (NowMs() - _lastActivityMs) / 1000.0;

        // ── Outbound ──────────────────────────────────────────────────────────

        /// <summary>
        /// Send one message. <see cref="DeliveryMode.Reliable"/> is acked, ordered and
        /// automatically fragmented; <see cref="DeliveryMode.Unreliable"/> is a single
        /// best-effort datagram and must fit within the MTU.
        /// </summary>
        public void Send(byte[] payload, DeliveryMode mode) => Send(payload, mode, gameData: false);

        /// <summary>
        /// 发连接请求（凭证 JSON）。UDP 没有握手，这条就是握手：走可靠通道，丢了会重传，
        /// 中继验过才会给这条连接分配任何东西。必须是本 peer 发出的第一条可靠消息。
        /// </summary>
        public void SendHello(byte[] credentials)
        {
            lock (_gate)
            {
                if (_closed) return;
                if (_pending.Count >= _opts.MaxPendingMessages)
                {
                    Close("too many un-acked reliable messages");
                    return;
                }

                uint rseq = _sendSeq++;
                var pending = new Pending((byte[])credentials.Clone(), gameData: false) { Hello = true };
                _pending[rseq] = pending;
                SendReliable(rseq, pending);
            }
        }

        /// <summary><paramref name="gameData"/>: 打上 GameData 位，对端按对局数据帧解释（分片时每片都带）。</summary>
        public void Send(byte[] payload, DeliveryMode mode, bool gameData)
        {
            lock (_gate)
            {
                if (_closed) return;

                if (mode == DeliveryMode.Unreliable)
                {
                    if (payload.Length > _opts.MaxPayload)
                    {
                        throw new ArgumentException(
                            $"Unreliable message of {payload.Length} bytes exceeds MTU payload {_opts.MaxPayload}. "
                            + "Use DeliveryMode.Reliable for large messages.");
                    }

                    uint seq = _unreliableSeq++;
                    FrameFlags flags = gameData ? FrameFlags.GameData : FrameFlags.None;
                    int written = EncodeFrame(_scratch, flags, seq, 0, 1, payload);
                    SendBytes(_scratch, written);
                    return;
                }

                if (_pending.Count >= _opts.MaxPendingMessages)
                {
                    Close("too many un-acked reliable messages");
                    return;
                }

                uint rseq = _sendSeq++;
                var pending = new Pending((byte[])payload.Clone(), gameData);
                _pending[rseq] = pending;
                SendReliable(rseq, pending);
            }
        }

        // ── Inbound ───────────────────────────────────────────────────────────

        /// <summary>Feed one received datagram. Called by whoever owns the socket.</summary>
        public void HandleDatagram(byte[] data, int length)
        {
            List<(byte[] Data, DeliveryMode Mode, bool GameData)> toDeliver = new List<(byte[] Data, DeliveryMode Mode, bool GameData)>();
            bool closedByPeer = false;
            string? byeReason = null;

            lock (_gate)
            {
                if (_closed) return;
                _lastActivityMs = NowMs();
                BytesReceived += length;

                if (!UdpFrame.TryRead(data.AsSpan(0, length), out var header, out var payload))
                {
                    return; // garbage datagram; ignore
                }

                ApplyAck(header.Ack, header.AckBitfield);

                if ((header.Flags & FrameFlags.Bye) != 0)
                {
                    // 对端主动告别：立刻关掉，别等 idle 超时。带 payload 时那就是理由
                    // （中继拒绝连接就是这么回话的：一个 Bye + reason，不建任何状态）。
                    closedByPeer = true;
                    if (payload.Length > 0) byeReason = Encoding.UTF8.GetString(payload.ToArray());
                }
                else if ((header.Flags & FrameFlags.Ping) != 0)
                {
                    // 保活：立刻回一个 ack 帧。对端靠这个回应确认我们还在
                    // （_lastActivityMs 已经在上面刷过了，这条连接对我们而言也是活的）。
                    int pong = EncodeFrame(_scratch, FrameFlags.AckOnly, 0, 0, 0, ReadOnlySpan<byte>.Empty);
                    SendBytes(_scratch, pong);
                    return;
                }
                else if ((header.Flags & FrameFlags.AckOnly) != 0)
                {
                    return;
                }
                else
                {
                    MessagesReceived++;
                    bool isGameData = (header.Flags & FrameFlags.GameData) != 0;
                    if ((header.Flags & FrameFlags.Hello) != 0)
                    {
                        // 连接请求：凭证已经由传输层在建这个 peer 之前验过了。这里只把它当
                        // 一条普通的可靠消息走序号和 ack（对端才会停止重传），不向上交付
                        // —— 上层不需要再看一遍自己的凭证。
                        HandleReliable(header.Seq, 0, 1, ReadOnlySpan<byte>.Empty, false, _discard);
                        _discard.Clear();
                    }
                    else if ((header.Flags & FrameFlags.Reliable) != 0)
                    {
                        HandleReliable(header.Seq, header.FragIndex, header.FragCount, payload, isGameData, toDeliver);
                    }
                    else
                    {
                        toDeliver.Add((payload.ToArray(), DeliveryMode.Unreliable, isGameData));
                    }
                }
            }

            if (closedByPeer)
            {
                Close(byeReason ?? "peer disconnected", notifyPeer: false); // 不回 Bye（对端已经关了）
                return;
            }

            foreach ((byte[] msg, DeliveryMode mode, bool isGameData) in toDeliver)
            {
                _deliver(msg, mode, isGameData);
            }
        }

        /// <summary>
        /// Drive retransmits, flush a pending ack, and enforce the idle timeout.
        /// Call periodically (the relay does it on its main loop, the client from Poll).
        /// </summary>
        public void Tick()
        {
            string? closeReason = null;

            lock (_gate)
            {
                if (_closed) return;

                long now = NowMs();

                if (now - _lastActivityMs > _opts.IdleTimeoutMs)
                {
                    closeReason = "idle timeout";
                }

                // Retransmit overdue reliable messages.
                foreach ((uint seq, Pending p) in _pending)
                {
                    if (now - p.SentAtMs >= _rtoMs)
                    {
                        SendReliable(seq, p);
                        _rtoMs = Math.Min(_rtoMs * 2, _opts.MaxRtoMs);
                    }
                }

                // 保活：这条连接安静太久就主动发一个 PING，把 NAT 映射和对端的 idle
                // 计时器一起续上。中继侧 KeepAliveMs 是 0，永远走不到这里。
                if (_opts.KeepAliveMs > 0 && now - _lastSentMs >= _opts.KeepAliveMs)
                {
                    int ping = EncodeFrame(_scratch, FrameFlags.Ping, 0, 0, 0, ReadOnlySpan<byte>.Empty);
                    SendBytes(_scratch, ping);
                }

                // Flush a standalone ack if we owe one and nothing piggybacked it.
                if (_ackDirty && now - _lastSentMs >= _opts.AckIntervalMs)
                {
                    int written = EncodeFrame(_scratch, FrameFlags.AckOnly, 0, 0, 0, ReadOnlySpan<byte>.Empty);
                    SendBytes(_scratch, written);
                    _ackDirty = false;
                }
            }

            if (closeReason != null)
            {
                Close(closeReason);
            }
        }

        /// <summary>Permanently mark the peer closed. Safe to call multiple times.</summary>
        public void Close(string reason) => Close(reason, notifyPeer: true);

        private void Close(string reason, bool notifyPeer)
        {
            lock (_gate)
            {
                if (_closed) return;

                if (notifyPeer)
                {
                    // Best-effort Bye：让对端立刻释放这条连接，而不是干等 idle 超时——
                    // 否则 UDP 下「断开后马上同名重连」会撞 name_taken。丢了就退回超时路径。
                    byte[] why = Encoding.UTF8.GetBytes(reason);
                    if (why.Length > _opts.MaxPayload) why = new byte[0];
                    int written = EncodeFrame(_scratch, FrameFlags.Bye, 0, 0, 0, why);
                    SendBytes(_scratch, written);
                }

                _closed = true;
                _pending.Clear();
                _incoming.Clear();
            }

            _close(reason);
        }

        // ── Reliable channel internals ────────────────────────────────────────

        private void HandleReliable(uint seq, ushort fragIndex, ushort fragCount, ReadOnlySpan<byte> payload, bool gameData, List<(byte[], DeliveryMode, bool)> toDeliver)
        {
            // 收包窗口。_incoming 只在 DeliverInOrder 里从 _recvNext 往上取，所以任何进得来、
            // 取不走的 seq 都是永久占位 —— 下面两条挡的就是这个。
            //
            // 守规矩的对端撞不到：它在 MaxPendingMessages 条未 ack 时就自己 Close 了（见 Send），
            // 因此最多领先 _recvNext (MaxPendingMessages - 1) 格。
            if (seq < _recvNext)
            {
                // 已经交付并移出表的旧包又回来了 —— 重传和 ack 擦肩而过，丢包时的常态。
                // 塞回表里就再也没人取走，所以只补一个 ack（累积 ack 本来就覆盖它，
                // 回给对端正好让它停止重传），不入表。
                _ackDirty = true;
                return;
            }

            if (seq - _recvNext >= (uint)_opts.MaxPendingMessages)
            {
                // 窗口之外。持续发高 seq、就是不补 _recvNext 那一格的源可以把 _incoming 撑爆，
                // 而 UdpTransport 是收到第一个包就建 peer —— 发生在任何鉴权之前。不 ack，直接丢。
                return;
            }

            if (fragCount <= 1)
            {
                if (!_incoming.ContainsKey(seq) || _incoming[seq].Payload == null)
                {
                    _incoming[seq] = Received.Complete(payload.ToArray(), gameData);
                    DeliverInOrder(toDeliver);
                }

                _ackDirty = true;
                return;
            }

            // Fragmented message. Guard against hostile fragCount.
            if (fragCount > 256 || fragIndex >= fragCount)
            {
                return;
            }

            if (!_incoming.TryGetValue(seq, out Received? existing))
            {
                existing = Received.Fragmented(fragCount, gameData);
                _incoming[seq] = existing;
            }

            if (existing.Fragments != null && existing.Fragments.Count < existing.FragCount
                && !existing.Fragments.ContainsKey(fragIndex))
            {
                existing.Fragments[fragIndex] = payload.ToArray();
                if (existing.Fragments.Count == existing.FragCount)
                {
                    DeliverInOrder(toDeliver);
                }
            }

            _ackDirty = true;
        }

        private void DeliverInOrder(List<(byte[], DeliveryMode, bool)> toDeliver)
        {
            while (_incoming.TryGetValue(_recvNext, out Received? r) && r.IsComplete)
            {
                byte[] message = r.Payload ?? Assemble(r.Fragments!);
                _incoming.Remove(_recvNext);
                _recvNext++;
                _ack = _recvNext - 1;
                toDeliver.Add((message, DeliveryMode.Reliable, r.GameData));
            }

            RebuildAckBitfield();
        }

        private static byte[] Assemble(Dictionary<int, byte[]> fragments)
        {
            int total = fragments.Values.Sum(f => f.Length);
            var buffer = new byte[total];
            int offset = 0;
            foreach ((int index, byte[] chunk) in fragments.OrderBy(kv => kv.Key))
            {
                chunk.CopyTo(buffer, offset);
                offset += chunk.Length;
            }

            return buffer;
        }

        private void ApplyAck(uint ack, uint bitfield)
        {
            if (_pending.Count == 0) return;

            var acked = new List<uint>();
            foreach (uint seq in _pending.Keys)
            {
                if (SeqLte(seq, ack))
                {
                    acked.Add(seq);
                }
                else
                {
                    uint delta = seq - (ack + 1);
                    if (delta < 32 && (bitfield & (1u << (int)delta)) != 0)
                    {
                        acked.Add(seq);
                    }
                }
            }

            if (acked.Count > 0)
            {
                foreach (uint seq in acked)
                {
                    _pending.Remove(seq);
                }

                _rtoMs = _opts.BaseRtoMs; // forward progress → reset backoff
            }
        }

        private void RebuildAckBitfield()
        {
            uint bitfield = 0;
            foreach (uint seq in _incoming.Keys)
            {
                if (SeqGt(seq, _ack) && seq - (_ack + 1) < 32)
                {
                    bitfield |= 1u << (int)(seq - (_ack + 1));
                }
            }

            _ackBitfield = bitfield;
        }

        private void SendReliable(uint seq, Pending p)
        {
            p.SentAtMs = NowMs();
            p.Retries++;

            FrameFlags baseFlags = FrameFlags.Reliable
                | (p.GameData ? FrameFlags.GameData : FrameFlags.None)
                | (p.Hello ? FrameFlags.Hello : FrameFlags.None);
            ReadOnlySpan<byte> payload = p.Payload;
            if (payload.Length <= _opts.MaxPayload)
            {
                int written = EncodeFrame(_scratch, baseFlags, seq, 0, 1, payload);
                SendBytes(_scratch, written);
                return;
            }

            int count = (p.Payload.Length + _opts.MaxPayload - 1) / _opts.MaxPayload;
            for (int i = 0; i < count; i++)
            {
                int offset = i * _opts.MaxPayload;
                int len = Math.Min(_opts.MaxPayload, p.Payload.Length - offset);
                FrameFlags flags = baseFlags;
                if (i == 0) flags |= FrameFlags.FragFirst;
                if (i == count - 1) flags |= FrameFlags.FragLast;
                int written = EncodeFrame(_scratch, flags, seq, (ushort)i, (ushort)count, payload.Slice(offset, len));
                SendBytes(_scratch, written);
            }
        }

        private int EncodeFrame(byte[] dst, FrameFlags flags, uint seq, ushort fragIndex, ushort fragCount, ReadOnlySpan<byte> payload)
        {
            var header = new FrameHeader(flags, seq, _ack, _ackBitfield, fragIndex, fragCount);
            return UdpFrame.Write(dst, header, payload);
        }

        private void SendBytes(byte[] data, int length)
        {
            _send(data, length);
            BytesSent += length;
            MessagesSent++;
            _lastSentMs = NowMs();
            _ackDirty = false;
        }

        private static long NowMs() => Clock.ElapsedMilliseconds;

        // Wraparound-safe uint sequence comparisons.
        private static bool SeqLte(uint a, uint b) => (int)(a - b) <= 0;
        private static bool SeqGt(uint a, uint b) => (int)(a - b) > 0;

        private sealed class Pending
        {
            public bool Hello { get; set; }

            public Pending(byte[] payload, bool gameData)
            {
                Payload = payload;
                GameData = gameData;
            }

            public byte[] Payload { get; }
            public bool GameData { get; }
            public long SentAtMs { get; set; }
            public int Retries { get; set; }
        }

        private sealed class Received
        {
            public byte[]? Payload { get; private set; }
            public Dictionary<int, byte[]>? Fragments { get; private set; }
            public int FragCount { get; private set; }
            public bool GameData { get; private set; }
            public bool IsComplete => Payload != null || (Fragments != null && Fragments.Count == FragCount);

            public static Received Complete(byte[] payload, bool gameData) => new Received { Payload = payload, GameData = gameData };

            public static Received Fragmented(int fragCount, bool gameData) =>
                new Received { Fragments = new Dictionary<int, byte[]>(), FragCount = fragCount, GameData = gameData };
        }
    }
}
