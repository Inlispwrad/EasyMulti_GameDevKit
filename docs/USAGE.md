# EasyMulti 使用文档

给小型游戏开发者。**你的负担只有三件事**：生成一个 token → 启动中继（传 token）→ 客户端里内置 token 和服务器地址。之后就只写客户端和 HostCore，中继不用管。

## 概念速览（30 秒）

- **中继只转发数据**，不跑任何游戏逻辑。谁当房主、谁算伤害、谁发牌，都是你自己的 HostCore 的事。
- **gameId** = 游戏命名空间。一台中继服务多个游戏，不同 gameId 的房间互不可见。
- **每个 Host 就是一个房间**。房主开房，其他人加入。按 gameId 拉房间列表，天然就是游戏大厅。
- **输入交给房主，结果由房主广播**。玩家只管 `Send` / `Received`，消息往哪儿路由是 SDK 的事；房主跑权威逻辑，所有人看到的顺序因此完全一致。
- **UDP 和 WebSocket 天然互通**：桌面/主机用 UDP（Steam / NS 高效路径），网页用 WebSocket，两边可以进同一个房间。

## 第 1 步：生成 token

token 是这台中继的「门禁密码」，谁有它谁能连。**它是你自己定的**，服务器不会发给你 ——
生成一个随机串，第 2 步喂给中继，第 3 步填进客户端，两边对上就能连。

Linux / macOS：

    openssl rand -hex 32

Windows（PowerShell，系统自带的 5.1 就行）：

    $b=[byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); ($b|%{$_.ToString('x2')}) -join ''

记下来，第 2 步和第 3 步都要用。不要提交进代码仓库（用环境变量 / 构建注入）。

> **只挡爬虫，不防专业黑客** —— 这不是免责声明，是架构的必然：第 3 步会把 token 编进你的
> 游戏客户端，谁把游戏拆包都能抠出来。对局内容真要防，得靠你自己的对局协议加密，别押在它上面。

## 第 2 步：启动中继（传 token）

**部署到服务器**：不用编译，也不用装 .NET —— 镜像由 CI 构建好放在 `ghcr.io`，服务器只要
Docker。拿 `deploy/` 下的 `docker-compose.yml` + `Caddyfile` + `.env` 三个文件，token 填进 `.env`，然后：

    docker compose up -d

**没部署过服务器？** 看手把手教程 [setup/zh.md](setup/zh.md)（[EN](setup/en.md) · [JA](setup/ja.md)），
从 SSH 讲起。参考手册见 [DEPLOY.md](DEPLOY.md)。跑起来之后重启自动拉起，**再也不用管它**。

**本地开发 / 内网调试**，直接从源码跑更快：

    EASYMULTI_TOKEN=你的token dotnet run --project src/EasyMulti.Relay -c Release

**机器得你自己买**——云主机、域名这些不在本项目范围。但买完之后怎么跑起来，
[DEPLOY.md](DEPLOY.md) 都写了：编排文件、TLS 反代、云主机防火墙（尤其 UDP 那条规则容易漏）、
以及镜像怎么拉。

> 网页要连 wss（HTTPS 页面只允许 wss），而中继只跑明文 ws：TLS 终结由你在前面加一层反代，见 [DEPLOY.md](DEPLOY.md)「反向代理」。

## 第 3 步：把 SDK 拷进项目，内置 token 和服务器地址

**SDK 就是源码，没有包也没有 DLL。** 把这 15 个文件拷进你的工程（Godot 放 `res://EasyMulti/`，Unity 放 `Assets/EasyMulti/`）：

    src/EasyMulti.Protocol/*.cs     （7 个）
    src/EasyMulti.Client/*.cs       （8 个）

它是 `netstandard2.1` + C# 9，零第三方依赖，Unity 2021.3+ / Godot 4.x / 纯 .NET 都能直接编。

游戏代码只碰一个静态门面和它开出来的两个角色，三层职责已经替你分好：

|  | 是谁 | 管什么 | 不管什么 |
|---|---|---|---|
| `EasyMulti` | 产品本身（全局静态门面） | `Init` 存配置、每帧 `Poll()`、退出 `Shutdown()` | — |
| `EasyMulti.Host.Open(name, …)` | 房主（一条**专门的连接**，不是玩家） | 开房、维护房间人员、跑核心逻辑 | 界面 |
| `EasyMulti.Client.Connect(name)` | 玩家 | 进房、退房，然后只剩收发消息 | **房间是怎么管的**——连「房主叫什么名字」都不暴露 |

    玩家 ──输入交给房主──▶ 房主(Core) ──结果广播给所有玩家──▶ 玩家

「自己开房自己玩」的写法是**先把 Host 开起来跑 Core，再用普通 Client 加进去**。玩家侧
代码因此永远没有一句「我是不是房主」的判断，本地房主和远端房主走的路完全一样；想换成
独立服务器时，把 Host 那半边挪进另一个进程即可，玩家侧一行不改。
host 不是玩家：不进玩家名单、不占 `players` 容量，人数不需要你做任何加减。

### 接进引擎：一个常驻节点 + 一份配置

整个游戏只有这一个文件碰中继地址（Godot 版，完整可运行的例子见
[samples/ChatGodot/](../samples/ChatGodot/README.md)）：

    using EasyMultiNet;
    using Godot;

    public partial class Net : Node
    {
        public override void _Ready() => EasyMulti.Init(new()
        {
            Token     = "你的token",
            GameId    = "my-game",
            RelayHost = "你的服务器地址",
            RelayPort = 7777,
            Codec     = new MemoryPackCodec(),   // 8 行拷贝件见下面「对局消息」一节
        });

        // 每帧驱动一次收发。SDK 的所有事件都在 Poll() 里、同线程回调出来，
        // 所以在回调里直接改 UI / 发信号是安全的，不需要跨线程调度。
        public override void _Process(double delta) => EasyMulti.Poll();

        public override void _ExitTree() => EasyMulti.Shutdown();
    }

Unity 里把 `_Process` / `_ExitTree` 换成 `Update()` / `OnDestroy()`，纯 .NET 里放进你
自己的主循环，其余一模一样。`Init` **只写入配置**，不建任何连接；重复调用就是整份覆盖。

#### 配置项

| 字段 | 默认 | 说明 |
|---|---|---|
| `Token` | — | 中继的共享密钥 |
| `GameId` | — | 房间的命名空间，不同 gameId 的房间互相看不见 |
| `RelayHost` | — | 中继地址，域名或 IP，**不带协议头** |
| `RelayPort` | `7777` | 反代后的 wss 一般填 `443` |
| `Transport` | `Udp` | `Udp` / `Ws` / `Wss`，见下表 |
| `Path` | `"/"` | WebSocket 专用：反代把中继挂在哪个子路径下（如 `"/em"`）。UDP 忽略 |
| `Codec` | 无 | 对局消息 body 的编解码器；不填就用不了 `Send<T>` / `Receive<T>`（fail-fast） |
| `TransportFactory` | 无 | 自带传输实现，填了盖过 `Transport`（见本节末） |

`Transport` 三选一：

| 取值 | 什么时候用 |
|---|---|
| `EasyMultiTransport.Udp`（默认） | 桌面 / 移动端。自带 ack+重传+分片，延迟最低 |
| `EasyMultiTransport.Ws` | UDP 被防火墙挡住，或部署平台不给 UDP 端口 |
| `EasyMultiTransport.Wss` | **浏览器 / WASM 导出**：HTTPS 页面只能连 `wss://`，明文会被混合内容策略拦掉 |

反代把中继挂在子路径下就填 `Path`：

    EasyMulti.Init(new()
    {
        Token = token, GameId = gameId, RelayHost = "你的域名", RelayPort = 443,
        Transport = EasyMultiTransport.Wss,
        Path      = "/em",
    });

**三种传输的玩家可以在同一间房里** —— 中继按房间路由，不看谁从哪条管子进来。服务端怎么配
wss 见 [DEPLOY.md](DEPLOY.md)。

Unity WebGL 那类运行时里 BCL 的 `ClientWebSocket` 用不了（没线程、没 socket，得桥接宿主 JS 的
WebSocket）：自己实现一个 `IClientTransport`，填进 `TransportFactory = () => new 你的传输()`。

### 玩家侧（EasyMulti.Client.Connect）

    var me = EasyMulti.Client.Connect("Alice");   // 玩家 id 就在调用链里，连上即用
    me.RoomsChanged += rooms => { /* 渲染大厅（只会在你问过之后来） */ };
    me.Joined       += code  => { /* 进房了，游戏逻辑从这里开始 */ };
    me.Receive<WorldSnap>(snap => { /* 房主广播的快照：按类型直接到手 */ });
    me.HostDropped  += ()    => { /* 房主掉线，对局暂停等他回来 */ };
    me.Rejected     += why   => { /* 名字被占 / 房满 / 已开局 */ };

    me.RefreshRooms();   // 动作都不必等连上：没注册完先攒着，注册成功后按序补发
    me.Join("ABC123");
    me.Send(new MoveMsg(3, -7));   // 直接发对象：T 就是消息通道

### HostCore（房主侧，EasyMulti.Host.Open）

`Open` = 建连 + 开房一步到位，返回房主实例（一条连接＝一间房）。权威逻辑写在
`Received` 里。名字和某个玩家一样＝那个玩家顺手开的房（host 连接自动带 `#host`
后缀，两条连接互不打架）：

    var room = EasyMulti.Host.Open("Alice", "我的房间", players: 4);  // 4 就是 4 个玩家
    room.Opened         += code => { /* 房码给玩家，他们就能进来 */ };
    room.PlayersChanged += players => { /* 玩家名单变了（房主从来不在里面） */ };
    room.PlayerBack     += id => room.Send(id, new WorldSnap(...));   // 重连者补发局面
    room.Receive<MoveMsg>((from, move) =>
    {
        // 权威循环：T 就是消息通道，按类型到手；算完回发。管道零膨胀直通。
        room.Broadcast(new WorldSnap(...));   // 发给所有玩家
        room.Send(from, new AckMsg(...));     // 只发某个人
    });

### 单独部署 Host（核心后端 / 专服）

把 HostCore 放进一个独立进程常驻服务器：**和客户端共用同一份 `Init` 配置**，开房时声明
`HostMode.StandAlone` —— 它没有「玩家人格」，中继会拒绝与它同名的玩家进房（`name_reserved`）：

    EasyMulti.Init(new()                              // 与客户端一模一样的一份配置
    {
        Token = token, GameId = gameId, RelayHost = relayHost, RelayPort = 7777,
        Codec = new MemoryPackCodec(),
    });

    var room = EasyMulti.Host.Open("Server-1", "Ranked #1", players: 8, HostMode.StandAlone);
    room.Receive<MoveMsg>((from, move) => { /* 权威循环 */ });
    room.PlayerBack += id => room.Send(id, new WorldSnap(...));   // 重连坐回，补发局面

    while (true) { EasyMulti.Poll(); Thread.Sleep(1); }

一条 Host 连接＝一间房；要常驻多间就多 `Open` 几个（同一进程即可）。断线走 `Disconnected`
事件，要不要自动重开由你的进程自己决定（再 `Open` 一个就是了）。

### 浏览器（网页 / wss）

把 `examples/Chat/web/easymulti.js` 拷进你的网页项目，它是零依赖的 WebSocket 客户端：

    <script src="easymulti.js"></script>
    <script>
      const client = new EasyMulti.EasyMultiClient({
        url: "wss://你的域名/relay",   // 本地测试可用 ws://你的服务器:7777/
        token: "你的token", gameId: "my-game", playerId: "Alice"
      });
      client.onRegistered = () => client.joinRoom("ABC123");
      client.onGameData = (from, data) => { /* 处理 */ };
      client.connect();
    </script>

浏览器是事件驱动的，不用 Poll。

## API 速查

`EasyMulti`（全局门面）：

| 成员 | 说明 |
|---|---|
| Init(EasyMultiConfig) | **只写入配置**，不建连接；重复调用就是整份覆盖。配置对象原地 `new(){…}`，字段见上面「配置项」 |
| Codec | 只读：当前挂着的编解码器（写入点只有 `Init` 的 `Codec` 字段） |
| Client.Connect(playerId) | 连上中继，返回玩家实例（下表） |
| Host.Open(name, title, players, mode?) | 连上中继并开一间房，返回房主实例（下表）；`HostMode.StandAlone`＝独立部署 |
| Poll() | 每帧驱动所有连接；所有事件在这里、同线程回调 |
| Shutdown() | 断开并释放所有连接（配置保留） |

`EasyMultiClient`（玩家实例，`EasyMulti.Client.Connect` 返回）：

| 成员 | 说明 |
|---|---|
| RefreshRooms() | 要一份房间列表（服务器只在被问到时才回） |
| Join(roomCode) / Leave() | 进房 / 退房回大厅 |
| Send&lt;T&gt;(value) | 直接发消息对象（SDK 定向发给房主；没进房时是空操作）。**T 就是消息通道** |
| Receive&lt;T&gt;(handler) | 订一条类型通道；未订阅的类型静默丢弃，同 T 多订＝叠加 |
| Disconnect() | 下线（回主菜单/切账号）；想再上线就再 Connect 一个 |
| Id / Connected / RoomCode | 你的 playerId / 中继认下你没 / 当前房码（不在房时 null） |
| RoomsChanged(rooms) | 列表来了——**只会在你调过 RefreshRooms() 之后触发** |
| Joined(code) / Left | 进房了（游戏逻辑从这开始）/ 退房了 |
| HostDropped / HostBack | 房主掉线（对局暂停，你还在房里）/ 房主回来了 |
| Rejected(reason) / Disconnected(reason) | 某个请求被拒（连接还在）/ 连接断了 |

`EasyMultiHost`（房主实例，`EasyMulti.Host.Open` 返回；一条连接＝一间房）：

| 成员 | 说明 |
|---|---|
| Send&lt;T&gt;(playerId, value) / Broadcast&lt;T&gt;(value) | 发给一个玩家 / 发给所有玩家（直接发对象） |
| Receive&lt;T&gt;((fromId, value)) | 订一条类型通道——核心逻辑从这开始 |
| Kick(player) | 把玩家移出房间（中继不做超时，掉线的什么时候清走由你决定） |
| Lock() | 封盘：大厅里标成进行中，新人不能再进（**只关入口**，开局是你的事） |
| Close() | 解散房间并断开：玩家全部被送回大厅，房间销毁 |
| Opened(code) | 房间开好了，这是房码 |
| PlayersChanged(players) | 玩家名单变了（房主不是玩家，名单里从来没有它） |
| PlayerDropped(name) / PlayerBack(name) | 玩家掉线（座位保留）/ 重连坐回（在这补发局面） |
| Rejected(reason) / Disconnected(reason) | 某个请求被拒 / 连接断了 |

测试、工具、或确实要整个协议面（状态机、玩家名单、HostId、autoHostTransfer、
不可靠通道）的高级用法，可以直接用低层 `RelaySession`——`EasyMulti` 门面就建立
在它上面，成员与线协议一一对应（见 [PROTOCOL.md](PROTOCOL.md)）。

## 常见问题

- **怎么定义消息？** 两端共用一份 `[MemoryPackable] partial record MoveMsg(int X, int Y);` 这样的类型定义——**T 就是消息通道**：`Send<MoveMsg>` 出、`Receive<MoveMsg>` 进，没订阅的类型静默丢弃。默认壳的线上开销只有 payload 前 4 字节类型键，body 由配置里的 `Codec` 编码（推荐 MemoryPack，零膨胀直通）。想换 protobuf/自定义编码＝换 `Codec`；想连路由壳都自己定义＝用低层 `RelaySession` 裸字节（见 [PROTOCOL.md](PROTOCOL.md) §7）。
- **Host.Send 的 player 填什么？** 填对方注册用的 playerId。它是身份标识不是显示名——显示名（可改、可重复、可带表情）是你游戏层自己的事，对局协议里定向找人只认 playerId。
- **大消息？** UDP 下超过 ~1180 字节的可靠消息自动分片；WebSocket 走 TCP 没这个限制。门面的 Send/Broadcast 全走可靠通道；高频可丢的状态流用低层 `RelaySession.SendGameData(..., mode: Unreliable)`（仅 UDP 生效，WS 自动退化为可靠）。
- **房主掉线了怎么办？** 默认**不换人**：席位保留，玩家收到 `HostDropped`（对局暂停），房主同名重连回房后玩家收 `HostBack`（对局状态不迁移——补发是你 HostCore 的事）。想要「玩家客户端也带着 host 逻辑、房主掉线自动有人顶上」的形态，用低层 `RelaySession.CreateRoom(..., autoHostTransfer: true)`——中继会把首个在线玩家提出名单立为新 host（发 HostChanged）。
- **一台中继能跑几个游戏？** 随便多少，用 gameId 隔开。部署一次，所有小游戏共用。
- **大厅列表会自动更新吗？** 不会，而且这是故意的。服务器绝不主动推房间列表（一台中继要扛上千连接，推送会级联到崩，见 [PROTOCOL.md](PROTOCOL.md) §4）。想让大厅保持新鲜，就在你的大厅界面里定时调 RefreshRooms()——刷多勤由你定，开销也落在你自己头上。进了房间就别刷了。
- **大厅怎么筛「进行中」的房间？** 每间 `Room` 带 `Started` 字段，`rooms.Where(r => !r.Started)` 就是可加入的。封盘后的房间别人加不进（会被 game_already_started 拒掉，走 Rejected）。
- **房间外的人能往房里发消息吗？** 不能。中继只受理房间成员发的消息，也只转发给房间成员；离开房间的人发不进去、也收不到。
- **掉线了能重连回进行中的房间吗？** 能，而且不限时。掉线后名字还在房间名单里；用同一个名字重新 `EasyMulti.Client.Connect` + Join(房码) 就坐回原座位（即便已封盘）。谁真走了、什么时候踢，是 Host 用 Kick 决定的。
- **房间里没人了房间会没吗？** 会。只要没有任何在线成员（全掉线了），房间就自动销毁。所以单人掉线后房间直接没了，得重开；多人房里至少还有一个人在线，房间才留着等你重连。
- **重连后怎么补发局面？** 中继不参与（它只认名单）。Host 监听 PlayerBack(name)，把当前局面快照 `Send(name, 快照)` 发过去即可。
- **掉线会被别人顶名字吗？** 会——名字就是身份，任何知道 token 的人都能用同一个名字重连顶替你。这与「共享 token 只防爬虫」的安全模型一致。
