#nullable enable

namespace EasyMulti.Protocol;

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
    private readonly Action<byte[], int> _send;
    private readonly Action<byte[], DeliveryMode> _deliver;
    private readonly Action<string> _close;
    private readonly UdpPeerOptions _opts;
    private readonly object _gate = new();
    private readonly byte[] _scratch;

    private uint _sendSeq = 1;          // next reliable message seq
    private uint _unreliableSeq;        // next best-effort message seq
    private uint _recvNext = 1;         // next expected reliable seq
    private uint _ack;                  // cumulative ack == _recvNext - 1
    private uint _ackBitfield;
    private bool _ackDirty;
    private long _lastAckSentMs;

    private readonly SortedDictionary<uint, Pending> _pending = new();
    private readonly SortedDictionary<uint, Received> _incoming = new();

    private long _rtoMs;
    private long _lastActivityMs;
    private bool _closed;

    public UdpPeer(
        string address,
        Action<byte[], int> send,
        Action<byte[], DeliveryMode> deliver,
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

    /// <summary>Seconds since the last inbound datagram.</summary>
    public double IdleSeconds => (NowMs() - _lastActivityMs) / 1000.0;

    // ── Outbound ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Send one message. <see cref="DeliveryMode.Reliable"/> is acked, ordered and
    /// automatically fragmented; <see cref="DeliveryMode.Unreliable"/> is a single
    /// best-effort datagram and must fit within the MTU.
    /// </summary>
    public void Send(byte[] payload, DeliveryMode mode)
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
                int written = EncodeFrame(_scratch, FrameFlags.None, seq, 0, 1, payload);
                SendBytes(_scratch, written);
                return;
            }

            if (_pending.Count >= _opts.MaxPendingMessages)
            {
                Close("too many un-acked reliable messages");
                return;
            }

            uint rseq = _sendSeq++;
            var pending = new Pending((byte[])payload.Clone());
            _pending[rseq] = pending;
            SendReliable(rseq, pending);
        }
    }

    // ── Inbound ───────────────────────────────────────────────────────────────

    /// <summary>Feed one received datagram. Called by whoever owns the socket.</summary>
    public void HandleDatagram(byte[] data, int length)
    {
        List<(byte[] Data, DeliveryMode Mode)> toDeliver = new();

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

            if ((header.Flags & FrameFlags.AckOnly) != 0)
            {
                return;
            }

            MessagesReceived++;
            if ((header.Flags & FrameFlags.Reliable) != 0)
            {
                HandleReliable(header.Seq, header.FragIndex, header.FragCount, payload, toDeliver);
            }
            else
            {
                toDeliver.Add((payload.ToArray(), DeliveryMode.Unreliable));
            }
        }

        foreach ((byte[] msg, DeliveryMode mode) in toDeliver)
        {
            _deliver(msg, mode);
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

            // Flush a standalone ack if we owe one and nothing piggybacked it.
            if (_ackDirty && now - _lastAckSentMs >= _opts.AckIntervalMs)
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
    public void Close(string reason)
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _pending.Clear();
            _incoming.Clear();
        }

        _close(reason);
    }

    // ── Reliable channel internals ────────────────────────────────────────────

    private void HandleReliable(uint seq, ushort fragIndex, ushort fragCount, ReadOnlySpan<byte> payload, List<(byte[], DeliveryMode)> toDeliver)
    {
        if (fragCount <= 1)
        {
            if (!_incoming.ContainsKey(seq) || _incoming[seq].Payload == null)
            {
                _incoming[seq] = Received.Complete(payload.ToArray());
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
            existing = Received.Fragmented(fragCount);
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

    private void DeliverInOrder(List<(byte[], DeliveryMode)> toDeliver)
    {
        while (_incoming.TryGetValue(_recvNext, out Received? r) && r.IsComplete)
        {
            byte[] message = r.Payload ?? Assemble(r.Fragments!);
            _incoming.Remove(_recvNext);
            _recvNext++;
            _ack = _recvNext - 1;
            toDeliver.Add((message, DeliveryMode.Reliable));
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

        ReadOnlySpan<byte> payload = p.Payload;
        if (payload.Length <= _opts.MaxPayload)
        {
            int written = EncodeFrame(_scratch, FrameFlags.Reliable, seq, 0, 1, payload);
            SendBytes(_scratch, written);
            return;
        }

        int count = (p.Payload.Length + _opts.MaxPayload - 1) / _opts.MaxPayload;
        for (int i = 0; i < count; i++)
        {
            int offset = i * _opts.MaxPayload;
            int len = Math.Min(_opts.MaxPayload, p.Payload.Length - offset);
            FrameFlags flags = FrameFlags.Reliable;
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
        _lastAckSentMs = NowMs();
        _ackDirty = false;
    }

    private static long NowMs() => Environment.TickCount64;

    // Wraparound-safe uint sequence comparisons.
    private static bool SeqLte(uint a, uint b) => (int)(a - b) <= 0;
    private static bool SeqGt(uint a, uint b) => (int)(a - b) > 0;

    private sealed class Pending
    {
        public Pending(byte[] payload) => Payload = payload;
        public byte[] Payload { get; }
        public long SentAtMs { get; set; }
        public int Retries { get; set; }
    }

    private sealed class Received
    {
        public byte[]? Payload { get; private set; }
        public Dictionary<int, byte[]>? Fragments { get; private set; }
        public int FragCount { get; private set; }
        public bool IsComplete => Payload != null || (Fragments != null && Fragments.Count == FragCount);

        public static Received Complete(byte[] payload) => new() { Payload = payload };

        public static Received Fragmented(int fragCount) =>
            new() { Fragments = new Dictionary<int, byte[]>(), FragCount = fragCount };
    }
}
