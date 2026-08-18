#nullable enable

using EasyMultiNet.Protocol;
using EasyMultiNet.Relay.Transport;

namespace EasyMultiNet.Relay;

public enum RelayEventKind
{
    Connected,
    Message,
    Disconnected,
}

/// <summary>
/// An event handed from the transports' background I/O into the relay core's single
/// threaded main loop. Because everything funnels through this queue, the core's state
/// (<c>games</c> / <c>rooms</c> / <c>peers</c>) never needs a lock.
/// </summary>
public readonly record struct RelayEvent(
    RelayEventKind Kind,
    IRelayConnection Connection,
    string? Text,        // Kind == Message: control JSON; null for binary game data
    byte[]? Binary,      // Kind == Message: game-data frame [routing header + payload]; null for control JSON
    string Reason,       // Kind == Disconnected: human-readable cause; otherwise ""
    DeliveryMode Mode);  // Kind == Message: how it arrived (WebSocket is always Reliable)
