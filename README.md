# EasyMulti

轻量、易部署的小型多人联机**中继服务器** + 配套 C# SDK。给小型游戏开发者用：部署一台中继之后基本不用再管它，之后只写客户端和 HostCore 即可。

> **EasyMulti 只做数据转交。** 玩家自己起 Host（可以是某个客户端开房，也可以是独立服务器进程）。中继不解析游戏内容，也不跑任何游戏规则。

## 特性

- **双传输**：一条中继同时接受 **WebSocket**（浏览器 / Godot Web）与 **UDP**（Steam / NS / 桌面）。两者**天然互通**——一个 WebSocket 客户端可以加入一个 UDP Host 开的房间。
- **gameId 路由**：一台中继服务多个游戏。每个 gameId 是独立的房间命名空间，互不可见。
- **房间即 Host，大厅即房间列表**：每个 Host 建一个房间，按 gameId 拉房间列表，天然形成游戏大厅。
- **共享 token 鉴权**：部署时设一个 token，所有客户端携带它才能连上。**只防爬虫、不防专业黑客**（项目定位如此）。
- **零第三方依赖**：只靠 .NET 8 BCL（HttpListener + ClientWebSocket + Socket），docker build 即可跑。
- **UDP 可靠通道**：自带 ack + 重传 + 分片重组，控制消息可靠有序，高频游戏状态可走不可靠通道。

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

## 仓库结构

```
src/
  EasyMulti.Protocol/   线协议 DTO + JSON 编解码 + UDP 帧与可靠通道（UdpPeer）
  EasyMulti.Relay/      中继服务器（可执行程序）
  EasyMulti.Client/     客户端 SDK（WebSocket / UDP 双传输，host 与 client 共用）
examples/
  Echo/                 最小 hostCore + client 示例
tests/
  EasyMulti.Tests/      集成测试（WS 与 WS、UDP 与 UDP、WS 与 UDP 互通、鉴权、隔离、分片）
```

## 文档

| 文档 | 内容 |
|---|---|
| [docs/PROTOCOL.md](docs/PROTOCOL.md) | 线协议：消息集合、字段语义、UDP 帧格式 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 设计：传输抽象、事件循环、可靠通道、互通为何免费 |
| [docs/DEPLOY.md](docs/DEPLOY.md) | 部署：配置来源、Docker、反向代理、安全边界 |

## 你的代码长什么样

```csharp
using EasyMulti.Client;

var cfg = new EasyMultiConfig(token: "demo-token", gameId: "my-game", playerName: "Host");
var client = EasyMultiClient.CreateUdp(cfg);   // 或 CreateWebSocket

client.RoomCreated += code => Console.WriteLine("房码 " + code);
client.GameDataReceived += (from, data) => { /* 权威循环：处理输入、回发结果 */ };

client.Connect("127.0.0.1", 7777);
// 主循环里每帧调用 client.Poll()，注册成功后 client.CreateRoom() 即成为 Host
```

## 安全边界

- token 是**共享密钥**，知道它的人都能连。它只用来挡住爬虫和脚本乱扫，**不防专业人工黑客**。
- 中继不解析 GAME_DATA.data，你的对局协议版本由两端自己校验。
- 生产环境请在反向代理（nginx / caddy）后面终结 wss://，中继自身只跑明文 ws://。

## License

MIT — 见 [LICENSE](LICENSE)。
