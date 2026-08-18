# EasyMulti

轻量、易部署的小型多人联机**中继服务器** + 配套 C# SDK。给小型游戏开发者用：部署一台中继之后基本不用再管它，之后只写客户端和 HostCore 即可。

> **EasyMulti 只做数据转交。** 玩家自己起 Host（可以是某个客户端开房，也可以是独立服务器进程）。中继不解析游戏内容，也不跑任何游戏规则。

## 来源

EasyMulti 是从 PokerRush 的 **DevRelay**（一个开发期 WebSocket 中继）演化来的，保留了它的核心设计——单线程事件循环、零第三方依赖、GAME_DATA 信封、`players[0]` 即房主、可靠/不可靠通道；并做了几处扩展与简化：

- **新增**：gameId 路由、token 鉴权、UDP 传输（DevRelay 已切到 WebSocket-only）、掉线重连（名单准入）、KICK、自动转交房主、无人即销毁。
- **去掉**：DevRelay 里 PokerRush 特有的 `SET_READY` / `ROOM_READY` 与「满员且全员准备才能开局」的开局条件——那套耦合 EasyMulti 不继承，准备状态和开局条件属于游戏层。

## 特性

- **双传输**：一条中继同时接受 **WebSocket**（浏览器网页 / WASM 导出）与 **UDP**（Steam / NS / 桌面）。两者**天然互通**——一个 WebSocket 客户端可以加入一个 UDP Host 开的房间。客户端一个配置项切换：`Transport = EasyMultiTransport.Udp / Ws / Wss`，HTTPS 页面用 `Wss`（TLS 由反代终结，见 [DEPLOY.md](docs/DEPLOY.md)）。
- **gameId 路由**：一台中继服务多个游戏。每个 gameId 是独立的房间命名空间，互不可见。
- **房间即 Host，大厅即房间列表**：每个 Host 建一个房间，按 gameId 拉房间列表，天然形成游戏大厅。
- **共享 token 鉴权**：部署时设一个 token，所有客户端携带它才能连上。**只防爬虫、不防专业黑客**（项目定位如此）。
- **SDK 就是源码，拷进项目就能用**：15 个 `.cs`、`netstandard2.1` + C# 9，Unity 2021.3+ / Godot 4.x / 纯 .NET 通吃。没有 NuGet 包、没有 DLL、没有 submodule。
- **傻瓜式全局门面**：`EasyMulti.Init(...)` 一次 → `EasyMulti.Client.Connect("Alice")`（玩家：进出房 + 收发消息）/ `EasyMulti.Host.Open("Alice", 房名, 人数)`（房主：开房 + 管人 + 跑核心逻辑），一句拿到角色实例。**host 不是玩家**——不进名单、不占容量，玩家侧永远不需要判断「我是不是房主」；单独部署的核心后端用 `HostMode.StandAlone` 声明。
- **零第三方依赖**：SDK 只用 BCL（Socket + ClientWebSocket），连 JSON 都是自带的（`Json.cs`）；中继另外只多用一个 BCL 的 `HttpListener`。`docker build` 即可跑。
- **UDP 可靠通道**：自带 ack + 重传 + 分片重组，控制消息可靠有序，高频游戏状态可走不可靠通道。
- **T 就是消息通道，零膨胀直通**：`Send<MoveMsg>` 出、`Receive<MoveMsg>` 进，默认壳按类型路由（未订阅静默丢）；body 走可插拔 `Codec`（配置项之一，默认推荐 MemoryPack），中继只读几字节路由皮、payload 一个字节不解析，没有 base64、没有 JSON 信封。

## 快速开始

### 1. 跑中继

```bash
dotnet run --project src/EasyMulti.Relay -- --token demo-token
```

默认 WebSocket 与 UDP 都监听 7777（TCP 与 UDP 是不同协议栈，可共用同一端口）。

### 2. 跑 Echo 示例（两个终端）

```bash
# 终端 A：起一个 Host（hostCore 示例）
dotnet run --project examples/Echo -- --mode host --name Host --transport udp

# 终端 B：起一个 Client，加入 Host 开的房间（把 <CODE> 换成终端 A 打印的房码）
dotnet run --project examples/Echo -- --mode client --name Alice --transport ws --room <CODE>
```

终端 B 每两秒发一条 ping，终端 A 回显 echo:...——这就是最小闭环。

### 3. Docker 部署

```bash
docker build -t easymulti .
docker run -d -p 7777:7777/tcp -p 7777:7777/udp -e EASYMULTI_TOKEN=your-secret easymulti
```

上面这条是本地 / 内网用的明文形态。**生产走 CI**：推代码 → GitHub Actions 跑测试并把镜像
推到 `ghcr.io` → 服务器只拉，不编译。服务器上只要 `docker-compose.yml` + `Caddyfile` + `.env`
三个文件（中继 + Caddy 自动签证书），然后：

```bash
docker compose up -d              # 首次
docker compose pull && docker compose up -d   # 以后每次更新
```

完整步骤（含镜像可见性、域名、防火墙）见 [docs/DEPLOY.md](docs/DEPLOY.md)。

## 仓库结构

```
src/
  EasyMulti.Protocol/   线协议 DTO + 自带 JSON 编解码 + UDP 帧与可靠通道（UdpPeer）
  EasyMulti.Relay/      中继服务器（可执行程序，net8.0）
  EasyMulti.Client/     客户端 SDK：EasyMulti 全局门面（Client / Host）+ RelaySession 低层 + 双传输
                        ↑ Protocol + Client 这两个目录里的 15 个 .cs 就是「SDK」，
                          netstandard2.1 + C# 9，拷进 Unity / Godot 工程即可
samples/
  ChatGodot/            Godot 4.7.1 聊天室 —— 全项目只有 Net.cs 一个文件碰中继
examples/
  Echo/                 最小 hostCore + client 示例
  Chat/                 聊天室 + 实时延迟（终端 UDP + 浏览器 WS，含基准工具）
tests/
  EasyMulti.Tests/      集成测试 + JSON 编解码测试（互通、鉴权、隔离、分片、转义、恶意输入）
```

## 文档

| 文档 | 内容 |
|---|---|
| [samples/ChatGodot](samples/ChatGodot/README.md) | **看这个最快**：Godot 4.7.1 聊天室，全项目只有一页碰中继 |
| [docs/USAGE.md](docs/USAGE.md) | **上手**：token → 部署 → 客户端内置配置，三步就完 |
| [docs/BENCHMARK.md](docs/BENCHMARK.md) | 延迟基准：UDP ~5ms、WS↔UDP ~10ms、WS↔WS ~17ms |
| [docs/PROTOCOL.md](docs/PROTOCOL.md) | 线协议：消息集合、字段语义、UDP 帧格式 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 设计：传输抽象、事件循环、可靠通道、互通为何免费 |
| [docs/DEPLOY.md](docs/DEPLOY.md) | 部署：配置来源、Docker / Compose、反向代理与 wss、云主机防火墙、安全边界 |

## 你的代码长什么样

理想形态是：**整个游戏只有一个文件碰中继**，其余全是前端和 Core。
[samples/ChatGodot/Net.cs](samples/ChatGodot/Net.cs) 就是那一个文件——四行常量加三句话：

```csharp
public override void _Ready() => EasyMulti.Init(new()
{
    Token     = "demo-token",
    GameId    = "chat-godot",
    RelayHost = "127.0.0.1",
    RelayPort = 7777,
    Codec     = new MemoryPackCodec(),   // 浏览器 / WASM 再加 Transport = EasyMultiTransport.Wss
});

public override void _Process(double delta) => EasyMulti.Poll();
public override void _ExitTree()            => EasyMulti.Shutdown();
```

`EasyMulti` 就是产品本身：`Client.Connect` / `Host.Open` 一句拿到角色实例，三层职责已经分好：

```
EasyMultiClient —— 玩家（EasyMulti.Client.Connect("Alice") 返回）
  动作（你调用）                     状态（你随时可问）      频道（你订阅，消息推给你）
  ─────────────────────────         ──────────────────     ────────────────────────────────
  RefreshRooms()   要房间列表        Id       你的 playerId    RoomsChanged(rooms) 列表来了（只在你问过之后）
  Join(roomCode)   申请进房间        Connected 认下了没      Joined(roomCode)    进房间了 ← 游戏逻辑从这开始
  Leave()          退房回大厅        RoomCode 当前房码       Left                退房了，回到大厅
  Send<T>(value)   直接发消息对象    （连上中继就等于在      Receive<T>(handler) 订类型通道：房主发的 T 从这进
  Disconnect()     下线               大厅，没有频道去       HostDropped / HostBack 房主掉线 / 回来了
                                      播报它）              Rejected / Disconnected 被拒（连接还在）/ 断线

EasyMultiHost —— 房主（EasyMulti.Host.Open("Alice", 房名, 人数[, HostMode.StandAlone])；一条连接＝一间房）
  动作（你调用）                     频道（你订阅，消息推给你）
  ─────────────────────────         ────────────────────────────────
  Send<T>(id, value)   发给一个人    Opened(roomCode)        房间开好了，这是房码
  Broadcast<T>(value)  发给所有人    PlayersChanged(players) 玩家名单变了（房主从来不在里面）
  Kick(player)         请人出去      PlayerDropped(name)     玩家掉线（座位保留）
  Lock()               封盘不再进人  PlayerBack(name)        玩家重连坐回 ← 在这补发局面
  Close()              解散房间      Receive<T>((who, v))    订类型通道 ← 核心逻辑从这开始
                                     Rejected / Disconnected 被拒 / 断线
```

所有「动作」**都不必等连上**——没注册完就先攒着，注册成功后按调用顺序补发。

于是「自己开房自己玩」的写法是 **先把 Host 开起来，再用普通 Client 接进去**：

```csharp
// 我来开房（名字和我的玩家名一样 = 我顺手开的；连接名自动带 #host，互不打架）
var room = EasyMulti.Host.Open(myName, title, players: 8);
room.Opened += code => { core = new ChatCore(room); me.Join(code); };

// 我这个玩家 —— 加自己开的房和加别人的房，是同一句
var me = EasyMulti.Client.Connect(myName);
me.Join(code);

// 说话。发到哪儿去不是玩家该操心的事
me.Send(text);
```

**玩家侧因此没有一句「我是不是房主」的判断**，本地房主和远端房主走的路完全一样；
想改成独立服务器，就是把 Host 那半边挪进独立进程、开房时声明 `HostMode.StandAlone`
（中继会替它拒掉同名玩家），玩家侧一行都不用改。

## 安全边界

- token 是**共享密钥**，知道它的人都能连。它只用来挡住爬虫和脚本乱扫，**不防专业人工黑客**。
- 中继不解析 GAME_DATA.data，你的对局协议版本由两端自己校验。
- 生产环境请在反向代理（nginx / caddy）后面终结 wss://，中继自身只跑明文 ws://。

## License

MIT — 见 [LICENSE](LICENSE)。
