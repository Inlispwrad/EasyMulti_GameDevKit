#nullable enable

using EasyMultiNet.Protocol;
using EasyMultiNet.Relay;

namespace EasyMultiNet.Relay.Transport;

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
    /// <summary>
    /// Begin accepting connections and pushing events via <paramref name="enqueue"/>.
    /// <para>
    /// <paramref name="authenticate"/>（凭证, 来源地址）→ null 放行 / 理由字符串拒绝。**在传输层
    /// 分配任何东西之前**调用：凭证随连接请求一起到，验不过的连接压根不存在，因此没有匿名连接
    /// 能占住的槽。它跑在传输的 I/O 线程上，所以只许看配置（token、格式），不许碰中继核心的状态
    /// —— 名字撞车那类判断留给核心线程上的 Connected 处理。
    /// </para>
    /// </summary>
    void Start(Action<RelayEvent> enqueue, Func<RegisterRequest, string, string?> authenticate);

    /// <summary>Stop accepting and close all connections.</summary>
    void Stop();

    /// <summary>
    /// Drive background bookkeeping (UDP retransmits / idle timeouts) on the core's
    /// main loop. WebSocket is a no-op.
    /// </summary>
    void Tick();
}
