# EasyMulti 使用文档

给小型游戏开发者。**你的负担只有三件事**：生成一个 token → 把中继扔到公网服务器 → 客户端里内置 token 和服务器地址。之后你就只写客户端和 HostCore，中继再也不用管。

## 概念速览（30 秒）

- **中继只转发数据**，不跑任何游戏逻辑。谁当房主、谁算伤害、谁发牌，都是你自己的 HostCore 的事。
- **gameId** = 游戏命名空间。一台中继服务多个游戏，不同 gameId 的房间互不可见。
- **每个 Host 就是一个房间**。房主开房，其他人加入。按 gameId 拉房间列表，天然就是游戏大厅。
- **players[0] 恒是房主**。房主跑了权威逻辑，玩家把输入定向发给房主，房主把结果广播回来。
- **UDP 和 WebSocket 天然互通**：桌面/主机用 UDP（Steam / NS 高效路径），网页用 WebSocket，两边可以进同一个房间。

## 第 1 步：生成 token

token 是这台中继的「门禁密码」，谁有它谁能连。只挡爬虫、不防专业黑客（项目定位如此）。

    openssl rand -hex 32

记下来，第 2 步和第 3 步都要用。不要提交进代码仓库（用环境变量/构建注入）。

## 第 2 步：把中继部署到公网服务器

假设服务器装了 Docker，且放行了 UDP 与 TCP 的 7777 端口。

    git clone https://github.com/Inlispwrad/EasyMulti_GameDevKit.git
    cd EasyMulti_GameDevKit
    docker build -t easymulti .
    docker run -d --name easymulti --restart unless-stopped       -p 7777:7777/tcp -p 7777:7777/udp       -e EASYMULTI_TOKEN=你的token       easymulti

完事。之后中继就在后台跑，重启自动拉起，**再也不用管它**。

没有 Docker 就装 .NET 8 SDK，直接跑：

    export EASYMULTI_TOKEN=你的token
    dotnet run --project src/EasyMulti.Relay -c Release

> 网页（浏览器）要连 wss，需要在前面加一层反代终结 TLS（中继自己只跑明文 ws）。见 [DEPLOY.md](DEPLOY.md)「反向代理」。

## 第 3 步：客户端内置 token 和服务器地址

你的游戏里只有两样东西要写：**客户端**（玩家）和 **HostCore**（房主）。两者用的是同一个 SDK，区别只是房主多调一个 CreateRoom。

### C# 客户端（玩家侧，Godot / Unity / 纯 .NET 通用）

    using EasyMulti.Client;

    var client = EasyMultiClient.CreateUdp(new EasyMultiConfig(
        token: "你的token",
        gameId: "my-game",
        playerName: "Alice"));

    client.Registered += () => client.JoinRoom("ABC123");       // 或先 ListRooms 列大厅
    client.RoomJoined += code => Console.WriteLine("已加入 " + code);
    client.GameDataReceived += (from, data) => {
        // data 是你游戏层定义的字符串（例如 base64(JSON)），中继不碰它
        // 注意：这里收到的可能来自房主，也可能来自其他玩家（广播）
    };

    client.Connect("你的服务器域名", 7777);

    while (true) {           // Godot 里放 _Process / _PhysicsProcess
        client.Poll();       // 每帧调一次，驱动收发
        Thread.Sleep(1);
    }

网页客户端不用这个 C# 类，见下面「浏览器」。

### HostCore（房主侧）

房主就是「调用了 CreateRoom 的那个客户端」，并且是 players[0]。权威逻辑写在 GameDataReceived 里：

    using EasyMulti.Client;

    var client = EasyMultiClient.CreateUdp(new EasyMultiConfig("你的token", "my-game", "Host"));

    client.Registered += () => client.CreateRoom("我的房间", maxPlayers: 4);
    client.RoomCreated += code => Console.WriteLine("房码 " + code);   // 把这个码告诉玩家
    client.RoomPlayersChanged += players => Console.WriteLine("成员 " + string.Join(",", players));
    client.GameDataReceived += (from, data) => {
        // 权威循环：解析 from 的输入，算结果，回发。
        // 广播给所有人：client.SendGameData("结果", to: null)
        // 只发某个人：   client.SendGameData("结果", to: from)
    };

    client.Connect("你的服务器域名", 7777);
    while (true) { client.Poll(); Thread.Sleep(1); }

Host 可以是「某个玩家的客户端顺手开房」，也可以是「独立服务器进程」——代码完全一样。

### 浏览器（网页 / wss）

把 `examples/Chat/web/easymulti.js` 拷进你的网页项目，它是零依赖的 WebSocket 客户端：

    <script src="easymulti.js"></script>
    <script>
      const client = new EasyMulti.EasyMultiClient({
        url: "wss://你的域名/relay",   // 本地测试可用 ws://你的服务器:7777/
        token: "你的token", gameId: "my-game", playerName: "Alice"
      });
      client.onRegistered = () => client.joinRoom("ABC123");
      client.onGameData = (from, data) => { /* 处理 */ };
      client.connect();
    </script>

浏览器是事件驱动的，不用 Poll。

## API 速查

EasyMultiClient（C#）常用成员：

| 成员 | 说明 |
|---|---|
| CreateUdp(config) / CreateWebSocket(config) | 建客户端（UDP / WebSocket） |
| Connect(host, port) | 连中继，自动 REGISTER |
| Poll() | 每帧驱动；所有事件都在 Poll 内回调 |
| CreateRoom(roomName, maxPlayers) | 开房，成为房主 |
| JoinRoom(code) / LeaveRoom() | 加入 / 离开房间 |
| RefreshRooms() | 拉取大厅房间列表 |
| SendGameData(data, to?, mode?) | 发对局数据；to 缺省=广播，mode 缺省=可靠 |
| StartGame() | 房主标记房间「开局」（阻止新加入、大厅可见） |

事件：

| 事件 | 触发 |
|---|---|
| Registered / Failed | 注册成功 / 失败（含断线） |
| RoomCreated / RoomJoined | 开房 / 加房成功 |
| RoomListChanged | 大厅列表变化（进大厅时、房间变化时） |
| RoomPlayersChanged | 有人进出（players[0] 恒房主） |
| GameStarted | 房主发了 StartGame |
| GameDataReceived(from, data) | 收到对局数据 |

属性：State、GameCode、RoomPlayers、Rooms、HostName、IsHost。

## 常见问题

- **GAME_DATA.data 里放什么？** 放你游戏层自己的编码，中继一个字节都不解析。推荐 base64(JSON)：`data = base64(utf8({"v":版本,"t":标签,"p":载荷}))`，版本不符由收件端自己丢包（见 [PROTOCOL.md](PROTOCOL.md) §7）。
- **定向消息 to 填什么？** 填对方的注册名 playerName。playerName 同时就是游戏层里的 playerId。
- **大消息？** UDP 下超过 ~1180 字节的可靠消息自动分片；WebSocket 走 TCP 没这个限制。高频状态建议 SendGameData(..., mode: Unreliable)（仅 UDP 生效，WS 自动退化为可靠）。
- **房主掉线了怎么办？** 中继自动把 players[0] 变成新 Host，但**对局状态不迁移**——那是你 HostCore 自己的事。
- **一台中继能跑几个游戏？** 随便多少，用 gameId 隔开。部署一次，所有小游戏共用。
