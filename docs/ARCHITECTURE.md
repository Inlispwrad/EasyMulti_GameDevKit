# EasyMulti 架构

## 总览

EasyMulti 分四块：

    ┌──────────────┐      ┌────────────────────────────────┐      ┌──────────────┐
    │  Client SDK   │      │          Relay (中继)           │      │   Client SDK  │
    │ (WebSocket)   │ ───▶ │  WebSocketTransport ─┐          │ ◀─── │   (UDP)       │
    └──────────────┘      │  UdpTransport ────────┤  events   │      └──────────────┘
                          │        └─▶ 主循环（单线程）        │
                          │             games → rooms → peers│
                          └────────────────────────────────┘

中继只做三件事：**认人**（token + gameId）、**管房**（每个 Host 一个房间、房间列表即大厅）、**搬数据**（GAME_DATA 原样转发）。

## 传输抽象

中继核心不认识 WebSocket 或 UDP，它只面对一个接口 `IRelayConnection`：

- `Send(string json, DeliveryMode mode)`
- 事件经 `RelayEvent`（Connected / Message / Disconnected）入队

两个 transport 各自负责把「传输特有的收发」翻译成「传输无关的消息 + 事件」：

| 关注点 | WebSocketTransport | UdpTransport |
|---|---|---|
| 连接身份 | 每个握手 = 一个连接 | 每个 (ip, port) = 一个连接（首包惰性收养） |
| 收 | `AcceptWebSocketAsync` + 接收泵 | `ReceiveFrom` 循环 → 交给 `UdpPeer` |
| 发 | 单读者队列 + 发送泵（保序） | `UdpPeer.Send` → `SendTo` |
| 可靠性 | TCP 自带 | `UdpPeer` 的 ARQ 通道 |
| 投递模式 | 恒可靠 | Reliable / Unreliable 分流 |

**互通为何「免费」**：房间成员列表存的是 `IRelayConnection`，转发时只发同一份 JSON，WebSocket 侧走文本帧、UDP 侧走二进制帧。应用层协议一致，所以跨传输自然成立。

## 单线程事件循环

所有后台 I/O（WS 泵、UDP 接收循环）只把 `RelayEvent` 塞进一个 `ConcurrentQueue`，主循环排干并分派。于是 `games` / `rooms` / `peers` 三个字典永远只被一个线程碰，无需加锁。

    后台 I/O ──enqueue──▶ ConcurrentQueue<RelayEvent> ──dequeue──▶ Dispatch（主循环）

主循环用 `Thread.Sleep(1)` 驱动，本地实测转发延迟：UDP↔UDP ~5ms、WS↔UDP ~10ms、WS↔WS ~17ms。**踩过的一个坑**（值得记住）：在 macOS 上 `Thread.Sleep(1)` 精确到 ~1.4ms，而 `SemaphoreSlim.Wait(1)` 与 `SpinWait.SpinUntil(…,1ms)` 会取整到 ~10ms——想用超时型等待做「亚毫秒唤醒」反而更慢。生产部署在 Linux 上，行为可能不同，但 `Thread.Sleep(1)` 在两边都够精确、够简单。

## 可靠 UDP 通道（UdpPeer）

`UdpPeer` 封装一条 UDP 会话，收发两端（中继与客户端）复用同一份实现：

- **发送**：可靠消息分配递增 seq，缓存未确认副本，超时重传（200ms 起 ×2 封顶 2000ms）；不可靠消息即发即弃。
- **接收**：可靠消息按序投递、乱序缓存；用累积 ack + 32 位选择性 ack 反馈；分片按 fragIndex 重组。
- **捎带 ack**：每个出向帧都带 ack/ackBitfield，只有确实无上行数据时才发 ACK_ONLY。
- **线程安全**：`Send` / `HandleDatagram` / `Tick` 可能来自不同线程，内部一把锁；回调不阻塞（只入队）。

## 关键实现决策

- **零第三方依赖**：WebSocket 用 BCL `HttpListener` + `ClientWebSocket`，UDP 用 BCL `Socket`，JSON 是自己写的（`Json.cs`，约 380 行）。目标有两个：把「部署」降到 `docker build` 一下，以及把「接入」降到「把 `.cs` 拷进工程」。手写 JSON 正是为了后者——`System.Text.Json` 不在 `netstandard2.1` 里，而任何 JSON 包都会在开发者和「拷进去就能用」之间插进一个 NuGet/UPM 步骤和一份 IL2CPP 裁剪配置。
- **SDK 目标框架是 `netstandard2.1` + C# 9**：这是「能被当作源码拷进现代 C# 引擎」的最大公约数——Unity 的编译器停在 C# 9，Godot 4.x / MonoGame / 纯 .NET 都能消费 netstandard2.1。中继本身不受此限，仍是 `net8.0`。
- **SDK 分三层，游戏代码只碰最上层**：全局门面（静态类 `EasyMulti`＝产品名：`Init` 只写配置；`Client.Connect(名字)` / `Host.Open(名字, 房名, 人数[, HostMode])` 一句拿到角色实例，名字走正常调用链；「三层职责」直接做进类型——玩家侧没有房间管理，连房主名字都不暴露；指令在注册完成前自动排队；`Rejected` 与 `Disconnected` 分频道）→ 会话（`RelaySession`，1:1 映射线协议的状态机，测试和高级用法直接用它）→ 传输（`IClientTransport`：UDP / WebSocket 可换，未来接 Godot WebSocketPeer 也在这层）。命名空间是 `EasyMultiNet`——产品名让给了门面类。
- **host 不是玩家，协议层就分开**：房间＝一条 host 连接 + N 个玩家席位。`players[]` / `playerCount` / `maxPlayers` 只算玩家，host 名单独走 `hostId`；host 掉线/回归有专门的 `HOST_DROPPED` / `HOST_BACK`。SDK 与界面因此不需要任何「±1 席位」「过滤 #host」的补丁。
- **gameId 是房间的命名空间**：`games[gameId] → rooms[code]`。不同 gameId 互不可见，一台中继服务多个游戏。
- **中继不施加游戏规则**：`START_GAME` 不校验人数/准备状态，只把房间标记为 `inGame`。准备状态、开局条件属于对局层（走 GAME_DATA）。中继一旦开始判断「几个人才能开局」，就等于把某一类游戏的规则焊进了通用设施里。
- **token 是共享密钥不是身份**：一个 token 一个中继，谁有 token 谁连。防爬虫靠 token + 按 IP 的错误尝试限流，不防专业攻击。
