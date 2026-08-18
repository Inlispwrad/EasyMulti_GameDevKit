# ChatGodot — Godot 4.7.1 聊天室

这个示例存在的唯一目的，是把 EasyMulti 想要的开发形态摆出来给你看：

> **部署一台中继 → 在一个文件里填上地址和 token → 之后只写前端和 Core。**

## 三层职责

| 谁 | 管什么 | 不管什么 |
|---|---|---|
| **中继** | 连接、消息路由、房间的建立与进出 | 完全不知道 host 和 client 之间在聊什么 |
| **Host** | 开房、维护房间人员、跑核心逻辑 | 不管界面 |
| **Client** | 进房、退房，然后只剩收发消息 | **完全不关心房间是怎么管的** |

这三层的边界由 SDK 的全局门面直接给出——`EasyMulti.Client.Connect(名字)` /
`EasyMulti.Host.Open(名字, 房名, 人数)` 一句拿到角色实例：

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

EasyMultiHost —— 房主（EasyMulti.Host.Open("Alice", 房名, 人数)；一条连接＝一间房，不是玩家：不进名单、不占容量）
  动作（你调用）                     频道（你订阅，消息推给你）
  ─────────────────────────         ────────────────────────────────
  Send<T>(id, value)   发给一个人    Opened(roomCode)        房间开好了，这是房码
  Broadcast<T>(value)  发给所有人    PlayersChanged(players) 玩家名单变了（房主从来不在里面）
  Kick(player)         请人出去      PlayerDropped(name)     玩家掉线（座位保留）
  Lock()               封盘不再进人  PlayerBack(name)        玩家重连坐回 ← 在这补发局面
  Close()              解散房间      Receive<T>((who, v))    订类型通道 ← 核心逻辑从这开始
                                     Rejected / Disconnected 被拒 / 断线
```

所有「动作」**都不必等连上**。中继在注册完成前会把房间指令静默丢弃，所以 SDK 把它们
攒起来、注册成功后按调用顺序补发——这类「忘了等就静默卡死」的坑不该留给使用者。

**玩家的游戏逻辑就两句**：`_me.Send(text)` 和 `_me.Receive<SayMsg>(...)`。消息路由到哪儿去，是 SDK 的事。

三个时刻别混：

| 时刻 | 含义 | 这时能做什么 |
|---|---|---|
| 传输连上 | socket 通了 | 不暴露，你不需要知道 |
| **`Connected` 变 true** | 中继认下身份，你在**大厅** | 纯给界面用（从「连接中…」切到大厅屏）。指令本来就会排队 |
| **`Joined`** | 进了房间 | ← **游戏逻辑的门卫是这个** |

Host 那边连大厅状态都没有：它注册完直接开房，第一个该等的信号就是 `Opened(code)`。

## 只看一个文件就够了

| 文件 | 它是什么 |
|---|---|
| **[Net.cs](Net.cs)** | **全项目唯一和中继地址打交道的地方**：四行常量 + `Init` / `Poll` / `Shutdown` 三句话。 |
| [ChatCore.cs](ChatCore.cs) | 权威逻辑（Core）。只在 Host 上跑，不知道中继/房码/Godot 的存在。 |
| [ChatMessages.cs](ChatMessages.cs) | 对局消息类型（[MemoryPackable] SayMsg / WhoMsg）+ 8 行 MemoryPackCodec。**T 就是消息通道**，中继一个字节都不解析。 |
| [Main.cs](Main.cs) | 玩家侧全部界面和逻辑。 |
| [Autopilot.cs](Autopilot.cs) | 只给 CI 用的无人值守驱动器，不是示例的一部分。 |

游戏文件只用到 `EasyMulti` 的 `Client` / `Host` / `Room` 三个门面类型；中继地址、token、
传输选择、连接的驱动与收尾，全部只出现在 `Net.cs` 这一页里。

## 自己开房自己玩 = 先起 Host，再用普通 Client 接进去

```csharp
// Main.cs —— 我来开房（名字用我的玩家名 = 我顺手开的房）
_host = EasyMulti.Host.Open(_me.Name, title, players: 8);   // 建连 + 开房一句话，不用等连上
_host.Opened += code =>
{
    _core = new ChatCore(_host);  // 权威逻辑起来了
    _me.Join(code);               // ↓ 从这里开始,和加入别人的房完全是同一条路
};

// Main.cs —— 我加入别人的房
_me.Join(code);

// Main.cs —— 说话
_me.Send(text);
```

**`Main.cs` 里没有任何一句「我是不是房主」的判断。** 想改成独立服务器，就是把
`_host` + `ChatCore` 挪到另一个进程，玩家侧一行都不用改。

### 这样换来了什么（可验证）

CI 跑两个进程，把两边收到的东西打出来：

```
host  : SAY seq=3 from=Guest ; SAY seq=4 from=HostPlayer
guest : SAY seq=3 from=Guest ; SAY seq=4 from=HostPlayer
```

序号由 Core 统一发放，**所有人看到的顺序完全一致**。而且 guest 是先 `SENT hello-from-guest`、
再收到 `SAY seq=3 from=Guest`——自己的话也是经房主回来的。玩家之间直接互发做不到这两点。

## 跑起来

### 1. 起中继

```bash
dotnet run --project ../../src/EasyMulti.Relay -c Release -- --token demo-token --port 7777
```

### 2. 开两个 Godot 实例

用 Godot 4.7.1（mono 版）打开本目录，按两次运行；或者：

```bash
godot --path samples/ChatGodot
```

一个开房、把房码告诉另一个，就能聊天。

### 3. 换成你自己的服务器

改 [Net.cs](Net.cs) 顶部那四行，别的地方一个字都不用动：

```csharp
private const string RelayHost  = "127.0.0.1";
private const int    RelayPort  = 7777;
private const string RelayToken = "demo-token";
private const string GameId     = "chat-godot";
```

## SDK 是怎么进到这个工程里的

没有 NuGet 包、没有 DLL、没有 submodule——**就是源码**。本示例的
[ChatGodot.csproj](ChatGodot.csproj) 直接 glob 仓库里的 `.cs`：

```xml
<Compile Include="../../src/EasyMulti.Protocol/*.cs" />
<Compile Include="../../src/EasyMulti.Client/*.cs" />
```

你自己的项目里，把那 15 个 `.cs` 拷进 `res://EasyMulti/` 就行，一样的效果。SDK
零第三方依赖（JSON 也是自带的），所以拷进去就能编。

## 一个要知道的细节

**Host 不是玩家**——它是一条专门的连接（注册名 `你的名字#host`），在协议层就和玩家分开：

- `Open(title, players: 8)` 就是 8 个玩家，房主不占数，没有任何加减；
- 大厅（`Room.Players` / `Room.Capacity`）与 `Host.PlayersChanged` 名单天然只有玩家；
- 房主掉线时玩家收 `HostDropped`（席位保留、对局暂停），同名重连回房后收 `HostBack`；
- 房主主动 `Close()` 则是解散：玩家全部回大厅，房间销毁。

## 自动化验收

```bash
godot --headless --path samples/ChatGodot res://Autopilot.tscn -- --role=host
godot --headless --path samples/ChatGodot res://Autopilot.tscn -- --role=guest --code=<房码>
```

两端都打印 `PASS` 才算通过：要求各自都收到了「自己的」和「对方的」发言，且序号一致。
它走的是和界面完全相同的那两扇门（`Client` / `Host` + `ChatCore`），所以它跑通就等于界面跑得通。
