# EasyMulti 线协议

本文是可编译契约（`src/EasyMulti.Protocol/RelayMessages.cs`）的规格说明。中继与客户端共用同一份 DTO，字段名 = 属性名的 camelCase（`JsonSerializerDefaults.Web`），值为 null 的可选字段不上线。

分两层，别混：

- **传输层**（本文）：REGISTER / 大厅 / 房间 / GAME_DATA **信封**，中继自己读。
- **对局层**：跑在 `GAME_DATA.data` 里的内容，中继**一个字节都不解析**。

---

## 1. 传输

| 传输 | 帧格式 | 可靠/有序 | 不可靠通道 |
|---|---|---|---|
| WebSocket | 一条报文一个 UTF-8 JSON 文本帧 | 恒 reliable ordered（TCP） | 无（Unreliable 自动退化为可靠） |
| UDP | 二进制帧（§6） | ack + 重传 + 分片重组 | 有（Unreliable，单帧 best-effort） |

两种传输的应用层消息完全一致，因此 WebSocket 客户端与 UDP 客户端可以加入同一个房间互通——互通对中继是免费的，因为中继把房间成员存成「传输无关的连接」，转发同一份 JSON。

- WebSocket URL 路径：`/`（反代挂子路径时随之改变）。子协议 `easymulti` + 一个凭证串，见 §3。中继不终结 TLS，`wss://` 由前置反向代理负责。
- UDP：客户端用同一个 socket 收发，中继按 `(ip, port)` 认连接（NAT 保持端口映射稳定）。

---

## 2. 客户端状态机

    连接请求（自带凭证，见 §3）
     ├─ 凭证不合格 → 连接不成立。中继不为它保留任何东西
     └─ 通过 → Lobby
                ├─ CREATE_ROOM → InRoom（这条连接成为 host，不进玩家名单）
                └─ JOIN_ROOM   → InRoom（玩家）
                                   └─ LEAVE_ROOM / 断线 → Lobby

**没有「连上了但还没证明身份」这个状态。** 凭证随连接请求一起到，中继验完才算连上 ——
否则匿名来源可以靠一直占着这种半截连接把连接数吃满，把真玩家挡在外面。

**host 不是玩家**：每个房间由一条专门的 host 连接 + 至多 `maxPlayers` 条玩家连接组成。
`players[]`、`playerCount`、`maxPlayers` 一律只算玩家；host 的名字单独走 `hostId` 字段。

---

## 3. 连接（鉴权 + gameId 路由）

凭证是这样一份 JSON：

    { "type": "REGISTER", "token": "...", "gameId": "my-game", "playerId": "Jin" }

- `token`：中继的共享密钥，错则拒。
- `gameId`：游戏命名空间，路由用。1–64 字符，`[A-Za-z0-9._-]`。
- `playerId`：玩家的**身份标识**，在该 gameId 内全局唯一，整个连接期不变。它就是对局层用来定向发消息的那个 id；玩家的**显示名**是游戏层自己的事，别拿它当显示名用。

它**随连接请求一起到**，两种传输各有各的搬法：

### WebSocket：`Sec-WebSocket-Protocol`

客户端在升级请求里提两个子协议名：固定的 `easymulti`，和 `em.` + base64url(凭证 JSON)。
中继在**升级之前**验，通过则回显 `easymulti` 完成升级，不通过回 `401` 且不升级。

    Sec-WebSocket-Protocol: easymulti, em.eyJ0b2tlbiI6ImRlbW8t...

为什么是子协议而不是头：浏览器的 `new WebSocket(url, protocols)` 设得了子协议，设不了自定义
HTTP 头。也因此凭证不会进 URL、不会落进反向代理的 access log。base64url 是必需的 ——
`playerId` 允许中文和空格，直接当子协议名不合 RFC 6455 的字符集。

> 反向代理必须透传 `Sec-WebSocket-Protocol`（caddy 的 `reverse_proxy` 默认透传）。

### UDP：HELLO 帧

UDP 没有握手，所以协议自己造了一个：陌生地址发来的**第一个**数据报必须是带 `HELLO` 标志位
（bit6，见 §6）的可靠帧，payload 就是上面那份凭证 JSON，且必须一个包装得下（不接受分片）。

不是 HELLO、解不开、或凭证不过 —— 中继**丢弃它，不建立任何状态**；验不过时回一个带理由的
`BYE` 帧（无状态，发完就忘）。验过了才创建连接，HELLO 本身按普通可靠消息 ack 但不向上交付。

### 两类失败，走两条路

| 失败 | 什么时候 | 怎么回 |
|---|---|---|
| `bad_token` / `bad_game_id` / `bad_request` | 门口 | **连接不成立**。WS 回 401，UDP 回带理由的 BYE。浏览器读不到 401 的原因（WebSocket 规范不把失败握手交给 JS）——但这几种只有开发者配错才会碰到，中继日志里有 |
| `name_taken` / `server_full` | 连接已建立后 | 不是鉴权失败：对方已经用有效 token 证明了身份。正常发 `REGISTER_FAILED { reason }` 再断，浏览器收得到，UI 可以提示换名字 |

连接被中继接受后会收到：

    { "type": "REGISTER_SUCCESS" }         // 不附带任何东西；房间列表要自己问（§4）

---

## 4. 大厅

**房间列表是「问才有」的：客户端发 `LIST_ROOMS`，服务器回一条 `ROOM_LIST`。除此之外服务器
在任何时刻都不会送出房间列表** —— 注册成功时不送，房间创建 / 有人进出 / 开局时也不推送。

这不是省事，是硬约束。一台中继要扛四位数的连接，推送意味着每次房间事件都把一份「房间数
大小」的包扇出给每个大厅连接；这些包堆在慢客户端的发送缓冲里（WebSocket 侧队列无界，UDP
侧攒够 MaxPendingMessages 就强断），而强断本身又是一次房间事件，于是再扇出一轮。内存增长
和断连互相喂，进程会崩在和当时任何人的操作都无关的时刻。**主动问的开销落在问的人身上，
被推送的开销落在所有人身上。**

要多新是客户端自己的决定：进大厅时问一次，之后按自己的节奏定时问。

### 房间摘要（ROOM_LIST）

    {
      "type": "ROOM_LIST",
      "rooms": [
        { "code": "ABC123", "name": "Room", "playerCount": 2, "maxPlayers": 4, "inGame": false, "hostId": "Jin#host" }
      ]
    }

`playerCount` / `maxPlayers` 只算玩家；`hostId` 是 host 连接的注册名。

---

## 5. 房间

### Client → Server

    { "type": "CREATE_ROOM", "roomName": "Jin's Test", "maxPlayers": 4, "autoHostTransfer": true }
    { "type": "JOIN_ROOM", "gameCode": "ABC123" }
    { "type": "LEAVE_ROOM" }
    { "type": "KICK", "playerId": "Tester2" }
    { "type": "START_GAME" }

| 字段 | 类型 | 必填 | 默认 | 说明 |
|---|---|---|---|---|
| CREATE_ROOM.roomName | string | 否 | "Room" | 房间显示名 |
| CREATE_ROOM.maxPlayers | int | 否 | 4 | **玩家**容量（host 不占数），服务端夹到 [1, 1024] |
| CREATE_ROOM.autoHostTransfer | bool | 否 | false | 房主掉线是否把首个在线玩家提拔为新 host；见「房主自动转交」 |
| CREATE_ROOM.dedicated | bool | 否 | false | 独立部署的 host（专服）：中继拒绝与 host 裸名同名的玩家加入该房（name_reserved） |

- `START_GAME`：仅 host 可发。中继**不**施加人数/准备条件（那是游戏逻辑），只把房间标记为 `inGame`（阻止新加入、大厅可见）。
- `LEAVE_ROOM`：**玩家**发＝退房回大厅（回 `LEAVE_SUCCESS`）；**host** 发＝**解散房间**——所有在线玩家被送回大厅（各收一份 `LEAVE_SUCCESS`），房间销毁。
- `ROOM_LIST` 里每个房间带 `inGame` 字段，客户端可按它筛「进行中」的房间；`START_GAME` 之后 `JOIN_ROOM` 会被 `game_already_started` 拒掉。

### Server → Client

    { "type": "ROOM_CREATED", "gameCode": "ABC123" }
    { "type": "JOIN_SUCCESS", "gameCode": "ABC123", "hostId": "Jin#host", "players": ["Tester1"] }
    { "type": "JOIN_FAILED", "reason": "room_full" }
    { "type": "PLAYER_JOINED", "playerId": "Tester1", "players": ["Tester1"] }
    { "type": "PLAYER_LEFT", "playerId": "Tester1", "players": [] }
    { "type": "PLAYER_DISCONNECTED", "playerId": "Tester1", "players": ["Tester1"] }
    { "type": "PLAYER_RECONNECTED", "playerId": "Tester1", "players": ["Tester1"] }
    { "type": "HOST_DROPPED" }
    { "type": "HOST_BACK" }
    { "type": "HOST_CHANGED", "hostId": "Tester1", "players": [] }
    { "type": "GAME_STARTED" }
    { "type": "LEAVE_SUCCESS" }

- 房间码由服务端生成：6 位大写字母 + 数字。
- `players[]` 恒为**玩家**名单（host 不在其中）；host 的名字在 `JOIN_SUCCESS.hostId` / `ROOM_LIST.hostId` 里。
- `JOIN_FAILED.reason` ∈ `room_not_found` / `room_full` / `game_already_started` / `name_taken` / `name_reserved`（dedicated 房间拒绝与 host 裸名同名的玩家）。
- **`#host` 后缀是协议约定**：host 连接的注册名 = 开房者名字 + `#host`（`RelayNaming.HostSuffix`）。中继按它剥出 host 的裸名做 dedicated 同名判定。

### 对局数据（GAME_DATA，二进制）

对局数据**不走 JSON**：它是带一层路由皮的原始字节，中继只读皮、换皮转发，
payload 一个字节不解析 —— MemoryPack 等任意二进制编码**零膨胀直通**（没有 base64、没有 JSON 转义）。

    载体：UDP 带 GAME_DATA 标志位的帧（§6） / WebSocket 的二进制帧（文本帧恒为控制 JSON）

    布局：[2B 小端 id 长度][id UTF8 字节][payload 原始字节]
      发送方向：id = 收件人（玩家或 host 的 id；长度 0 = 广播给同房其他所有连接）
      转发方向：id = 发件人（中继把皮换掉，payload 原样搬运）

- 只受理房间成员（host 或玩家）发的，也只转发给房间成员；不回显给发送者。
- UDP 下可选 Reliable（自动分片）或 Unreliable（高频状态，单帧）；WS 恒可靠。
- payload 里放什么、怎么编解码，永远是 host 和 client 两端自己的协议（§7）。

## 掉线重连与移除

中继**不做时间逻辑**，只认名单：名字在名单（玩家名单或 host 席位）上就能进来。掉线的人名字仍在名单里（座位保留），直到被移除。

1. **玩家**掉线 → 中继发 `PLAYER_DISCONNECTED`（他仍在 `players[]`，标记为「掉线」）；用同一份凭证重新连接 + `JOIN_ROOM(房码)` → 坐回原座位（即便已 `inGame`），回 `JOIN_SUCCESS` +（若已开局）`GAME_STARTED`，向其他人发 `PLAYER_RECONNECTED`。
2. **host** 掉线 → 玩家收 `HOST_DROPPED`（席位保留、房主没换）；host 用同一份凭证重新连接 + `JOIN_ROOM(房码)` → 坐回 host 席位，玩家收 `HOST_BACK`。对局状态补发由 host 逻辑自己做，中继不参与。
3. 移除玩家（谁真走了、什么时候踢）是 **host 逻辑**：host 发 `KICK { playerId }`，中继把该玩家移出名单、发 `PLAYER_LEFT`（在线者同时被送回大厅）。玩家自己 `LEAVE_ROOM` 也一样移除。host 不在玩家名单里，天然踢不到。
4. **房间里没有任何在线连接（host 和玩家都不在）→ 房间销毁**（僵尸房间清理）。

## 房主自动转交

开房时 `CREATE_ROOM.autoHostTransfer` 决定房主掉线后怎么办：

- **true**：房主掉线 → 中继把**首个在线玩家从玩家名单里提出来，立为新 host**，广播 `HOST_CHANGED { hostId, players }`（players 是提拔后的剩余玩家名单）。只对「玩家客户端也带着 host 逻辑、能接过工作」的形态有意义。
- **false（默认）**：房主掉线 → 席位保留、不换人（玩家收 `HOST_DROPPED`，等原房主重连或散伙）。**专服场景**（host 是独立服务器、和玩家分开）必须用这个。

> 同名即身份：任何持有 token 的人都能用同一个名字顶替（与共享 token 的安全模型一致，防爬虫不防冒名）。

---

## 6. UDP 帧格式（二进制）

所有多字节整数小端序。每帧承载一条消息（中继转发的单位），大消息分片、共用同一个序号、重组后再投递。

    字节 0       magic = 0xE9
    字节 1       version = 0x01
    字节 2       flags：bit0=RELIABLE  bit1=FRAG_FIRST  bit2=FRAG_LAST  bit3=ACK_ONLY
                        bit4=BYE（可带一段 UTF-8 理由）  bit5=GAME_DATA  bit6=HELLO（连接请求，见 §3）
                        bit7=PING（保活，见下）
    字节 3       reserved
    字节 4–7     seq（uint32）—— 消息序号
    字节 8–11    ack（uint32）—— 累积 ack（已按序投递的最后一个可靠序号）
    字节 12–15   ackBitfield（uint32）—— 选择性 ack（bit N = 已收到 ack+1+N）
    字节 16–17   fragIndex（uint16）
    字节 18–19   fragCount（uint16，≤1 表示未分片）
    字节 20+     payload

- 帧头 20 字节；默认 MTU 1200，故单帧 payload 上限约 1180 字节。超限的 Reliable 消息自动分片，Unreliable 消息必须塞进一帧。
- 可靠通道：发送方为每条消息分配递增 seq，未确认前缓存，超时（200ms 起，指数退避封顶 2000ms）重传；接收方按序投递、乱序缓存、用累积 + 选择性 ack 反馈。ack / ackBitfield 恒描述可靠通道，即使帧本身是不可靠游戏数据也顺带捎上 ack。
- 不可靠通道：flags 无 RELIABLE 位，即到即投、不重传、不保序，适合高频状态（更新的包自然取代丢失的包）。
- ACK_ONLY：无 payload，只用来在无上行数据时刷一个 ack。
- BYE：无 payload 的「告别」帧，主动断开时 best-effort 发一次（不重传）。对端收到立即释放这条连接，不必干等 idle 超时 —— 否则 UDP 下「断开后马上同名重连」会撞 `name_taken`。丢了就退回 idle 超时路径。
- GAME_DATA：payload 不是控制 JSON，而是对局数据（§5 的路由皮 + 原始字节）。可与 RELIABLE / 分片位组合；分片时每片都带。

---

## 保活（PING / ACK_ONLY）

UDP 连接闲置超过 `idleTimeoutMs`（默认 60 秒）会被判定为死连接并清掉。而
`lastActivity` 只被**收到的**数据报刷新 —— 自己发东西不算自己还活着。所以没有保活的话，
一间没人说话的房间一分钟后会全员掉线。

**客户端定时发 PING，中继收到后立刻回一个 ACK_ONLY。** 这个不对称是有原因的：

- 要保住的是**客户端那条 NAT 映射**，而 NAT 映射只能被出站包刷新。映射一旦过期，中继发
  什么都到不了客户端，也救不回来 —— 只有客户端自己能重建。真实设备的 UDP 映射超时大约
  从 30 秒起（RFC 4787 要求不短于 2 分钟，但消费级和运营商 NAT 普遍达不到）。
- **必须回应**，不能只发不回。客户端一直发、什么都收不到的话，会被自己的 idle 计时器误杀。
  一来一回同时证明两件事：中继知道客户端活着，客户端也知道中继活着。

间隔 15 秒（RFC 6263 给 UDP 的下限），对 60 秒的超时留了四倍余量，丢几个包也不会误判。

中继侧**不主动发** PING，只回应。它的 idle 超时因此继续担任「对方真没了」的检测器 ——
一个不再发 PING 的客户端就是没了。WebSocket 走 TCP，不需要这套。

## 7. 对局载荷约定（应用层，中继不参与）

payload 里放什么由游戏自定 —— 它就是你交给 SDK 的原始字节，线上只多一层路由皮。
**默认推荐 MemoryPack**（小、快、零分配）；protobuf、自定义二进制、UTF8 文本都行，
管道不偏向任何编码。版本号/消息标签之类的信封字段自己加在 payload 内，中继不校验、不认识，版本不符由收件端自己丢包。

落回传输层的三条约定：

1. 对局层直接沿用注册时的 `playerId` 当玩家标识，别再造一套 —— 否则 host 的定向 `to` 填不对人。
2. 权威引擎跑在 host 那条连接上（`JOIN_SUCCESS.hostId` 指认），应用层不再另行选举。
3. WebSocket 恒可靠有序，没有 MTU 问题；UDP 下大消息走 Reliable，高频状态走 Unreliable。
