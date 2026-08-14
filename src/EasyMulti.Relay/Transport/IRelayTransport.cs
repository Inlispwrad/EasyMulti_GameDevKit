#nullable enable

using EasyMulti.Relay;

namespace EasyMulti.Relay.Transport;

/// <summary>
/// A server-side transport that accepts client connections and feeds
/// <see cref="RelayEvent"/>s into the relay core via <paramref name="enqueue"/>.
/// <para>
/// The callback is the relay core's signaling enqueue: it appends to the event queue and
/// wakes the main loop immediately, so forwarding latency is not bounded by a poll timer.
/// </para>
/// </summary>
public interface IRelayTransport : IDisposable
{
    /// <summary>Begin accepting connections and pushing events via <paramref name="enqueue"/>.</summary>
    void Start(Action<RelayEvent> enqueue);

    /// <summary>Stop accepting and close all connections.</summary>
    void Stop();

    /// <summary>
    /// Drive background bookkeeping (UDP retransmits / idle timeouts) on the core's
    /// main loop. WebSocket is a no-op.
    /// </summary>
    void Tick();
}
