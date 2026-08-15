# 延迟基准

## 结论（本机 loopback 实测）

| 链路 | 平均 | p50 | p95 | 样本 |
|---|---|---|---|---|
| UDP ↔ UDP（桌面/主机游戏路径） | **4.6 ms** | 4 ms | 7 ms | 200 |
| WebSocket ↔ UDP（浏览器 ↔ 终端） | **10.2 ms** | 10 ms | 19 ms | 200 |
| WebSocket ↔ WebSocket | 16.9 ms | 16 ms | 30 ms | 200 |

UDP 是给 Steam / NS / 桌面准备的高效路径；WebSocket 是给网页展示路径。两者天然互通，所以「浏览器(WS) ↔ 终端(UDP)」这个混合场景也直接可用，约 10 ms。

> 这些是 loopback（本机回环）数字，衡量的是**中继自身的开销**。真实部署在这个基础上叠加网络往返（一般 20~100 ms）。中继本身的转发开销：UDP 侧 ~2-3 ms，WS 侧 ~5-8 ms。

## 测试方法

- 两台客户端加入同一个房间，互相广播 ping（带时间戳），对端定向回 pong，发 ping 的一方用「收 pong 时刻 − 发 ping 时刻」计 RTT。
- 每 10 ms 发一个 ping，采 200 个样本后算 min / avg / p50 / p95 / max。
- 环境：macOS（Apple Silicon）、.NET 8.0.411、Release 构建、loopback。终端侧用 1 ms poll，浏览器/Node 侧事件驱动。

复现（先起中继）：

    dotnet run --project src/EasyMulti.Relay -c Release -- --token demo-token

UDP↔UDP（两个终端客户端）：

    dotnet run --project examples/Chat -c Release -- --mode host   --name H --transport udp --bench 500 --ping-interval 10
    # 记下 ROOM_CODE，再开一个终端：
    dotnet run --project examples/Chat -c Release -- --mode client --name G --transport udp --bench 200 --ping-interval 10 --room ROOM_CODE

WS↔UDP（浏览器/Node ↔ 终端）：

    dotnet run --project examples/Chat -c Release -- --mode host --name H --transport udp --bench 500 --ping-interval 10
    node examples/Chat/web/bench.mjs --url ws://127.0.0.1:7777/ --name B --room ROOM_CODE --count 200 --interval 10

## 优化过程（为什么现在这么快）

第一版中继主循环用 `Thread.Sleep(15)` 轮询事件队列，每条转发最坏要多等 15 ms。一轮往返要经过 4 次中继转发，于是 WS↔UDP 一开始是 **avg 39 ms**。

改成 `Thread.Sleep(1)` 后：

| 阶段 | WS↔UDP avg |
|---|---|
| `Thread.Sleep(15)` 轮询 | 39 ms |
| 最终：`Thread.Sleep(1)` | 10 ms（UDP↔UDP 4.6 ms） |

### 一个反直觉的坑

想用「事件驱动唤醒」进一步压延迟，试过 `SemaphoreSlim.Wait(1)` 和 `SpinWait.SpinUntil(…, 1ms)`，结果**更慢**。实测这台 macOS 上：

| 等待方式 | 实际粒度 |
|---|---|
| `Thread.Sleep(1)` | ~1.4 ms |
| `SemaphoreSlim.Wait(1)` | ~10 ms |
| `SpinWait.SpinUntil(…, 1ms)` | ~10 ms |

即超时型等待在 macOS 上会取整到 ~10 ms，反而不如朴素的 `Thread.Sleep(1)`。生产部署在 Linux 上，`Thread.Sleep(1)` 同样精确（~1 ms）。结论：**中继主循环就用 `Thread.Sleep(1)`，别自作聪明。**

## UDP 可靠通道压测

UDP 的可靠通道（ack + 重传 + 分片重组）单独做过 200 msg/s 双向压测：未确认消息数恒为 0~1，零丢失，不涨内存。中继通过「未确认消息超上限」在 ~10 秒内发现已死客户端（比 60 s 空闲超时更快），会自动断开并清理。
