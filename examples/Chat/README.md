# EasyMulti Chat — 聊天室 + 实时延迟

一个最小但完整的示例：终端（UDP）+ 浏览器（WebSocket / wss）加入同一个房间，互发消息并实时显示 RTT。

## 目录

- Program.cs — 终端客户端（默认 UDP，可用 --transport ws 切 WebSocket）
- web/index.html + web/easymulti.js — 浏览器客户端（同一份 easymulti.js 也跑在 Node 里做基准）
- web/bench.mjs — 延迟基准（WebSocket 侧）
- web/serve.mjs — 本地静态服务器 + wss 反代（Node + openssl，免装 caddy）
- web/Caddyfile — 生产 wss 反代（caddy）

## 跑起来

第一步，起中继（另开一个终端）：

    dotnet run --project src/EasyMulti.Relay -- --token demo-token

第二步，起终端 Host（UDP）：

    dotnet run --project examples/Chat -- --mode host --name Host --transport udp

它会打印一行 ROOM_CODE=XXXXXX。把房码填到浏览器里。

第三步，起本地服务器：

    cd examples/Chat/web
    node serve.mjs

然后浏览器打开（wss，自签证书点「继续」）：

    https://localhost:8443/?token=demo-token&room=XXXXXX

或者要由浏览器建房，把 room 换成 host=1：

    https://localhost:8443/?token=demo-token&host=1

两侧每 1 秒互 ping 一次，实时显示到对方的 RTT。终端里输入文字回车即发消息，浏览器里输入框回车即发。

## 延迟基准

起中继后，跑 UDP Host + Node 测 WebSocket→UDP 往返：

    dotnet run --project examples/Chat -- --mode host --name H --transport udp --bench 500 --ping-interval 10
    # 记下 ROOM_CODE，然后：
    node web/bench.mjs --url ws://127.0.0.1:7777/ --name B --room ROOM_CODE --count 200 --interval 10

本机（macOS）实测：UDP↔UDP ~5ms、WS↔UDP ~10ms、WS↔WS ~17ms。

## 说明

- 应用层消息就是 GAME_DATA.data 里的纯 JSON：chat / ping / pong（见 Program.cs 顶部注释）。
- 延迟是「谁发 ping 谁计时」的对称测量：广播 ping，对端定向回 pong，收 pong 算 RTT。
- 终端默认 1ms poll；浏览器是事件驱动，不需要 poll。
