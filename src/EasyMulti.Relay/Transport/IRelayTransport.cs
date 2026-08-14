#nullable enable

using System.Collections.Concurrent;
using EasyMulti.Relay;

namespace EasyMulti.Relay.Transport;

/// <summary>
/// A server-side transport that accepts client connections and feeds
/// <see cref="RelayEvent"/>s into the relay core's queue.
/// </summary>
public interface IRelayTransport : IDisposable
{
    /// <summary>Begin accepting connections and pushing events into <paramref name="events"/>.</summary>
    void Start(ConcurrentQueue<RelayEvent> events);

    /// <summary>Stop accepting and close all connections.</summary>
    void Stop();

    /// <summary>
    /// Drive background bookkeeping (UDP retransmits / idle timeouts) on the core's
    /// main loop. WebSocket is a no-op.
    /// </summary>
    void Tick();
}
