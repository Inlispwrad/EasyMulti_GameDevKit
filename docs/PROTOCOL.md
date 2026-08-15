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

- WebSocket URL 路径：`/`，无子协议。中继不终结 TLS，`wss://` 由前置反向代理负责。
- UDP：客户端用同一个 socket 收发，中继按 `(ip, port)` 认连接（NAT 保持端口映射稳定）。

---

## 2. 客户端状态机

    连接
     └─ Unregistered   （只接受 REGISTER）
          └─ REGISTER 成功 → Lobby
                               ├─ CREATE_ROOM → InRoom（Host，players[0]）
                               └─ JOIN_ROOM   → InRoom（Client）
                                                  └─ LEAVE_ROOM / 断线 → Lobby

---

## 3. 注册（鉴权 + gameId 路由）

任何连接的第一条消息必须是 `REGISTER`：

    { "type": "REGISTER", "token": "...", "gameId": "my-game", "playerName": "Jin" }

- `token`：中继的共享密钥，错则拒。
- `gameId`：游戏命名空间，路由用。1–64 字符，`[A-Za-z0-9._-]`。
- `playerName`：在该 gameId 内全局唯一，注册后整个连接期不变；它同时也是对局协议里的 `playerId`。

响应：

    { "type": "REGISTER_SUCCESS" }         // 随即收到 ROOM_LIST
    { "type": "REGISTER_FAILED", "reason": "bad_token" }

`reason` ∈ `bad_token` / `bad_game_id` / `name_taken` / `server_full` / `bad_request`。

---

## 4. 大厅

注册成功后立即收到当前 gameId 的房间列表，房间变化时收到推送。主动刷新发 `LIST_ROOMS`。

### 房间摘要（ROOM_LIST / LOBBY_UPDATED）

    {
      "type": "ROOM_LIST",
      "rooms": [
        { "code": "ABC123", "name": "Room", "playerCount": 2, "maxPlayers": 4, "inGame": false, "hostName": "Host" }
      ]
    }

`LOBBY_UPDATED` 结构相同，在房间创建 / 有人进出 / 开局时向所有大厅客户端推送。

---

## 5. 房间

### Client → Server

    { "type": "CREATE_ROOM", "roomName": "Jin's Test", "maxPlayers": 4 }
    { "type": "JOIN_ROOM", "gameCode": "ABC123" }
    { "type": "LEAVE_ROOM" }
    { "type": "KICK", "playerName": "Tester2" }
    { "type": "START_GAME" }
    { "type": "GAME_DATA", "data": "<base64>" }
    { "type": "GAME_DATA", "to": "Tester2", "data": "<base64>" }

| 字段 | 类型 | 必填 | 默认 | 说明 |
|---|---|---|---|---|
| CREATE_ROOM.roomName | string | 否 | "Room" | 房间显示名 |
| CREATE_ROOM.maxPlayers | int | 否 | 4 | 人数上限，服务端夹到 [2, 1024] |
| GAME_DATA.data | string | 是 | — | 不透明载荷，中继不解析 |
| GAME_DATA.to | string | 否 | 缺省 | 缺省→广播给同房间其他成员；给定→定向给该玩家名 |

- `START_GAME`：仅 Host（`players[0]`）可发。中继**不**施加人数/准备条件（那是游戏逻辑），只把房间标记为 `inGame`（阻止新加入、大厅可见）。
- `GAME_DATA` 只受理房间成员发的（非成员发直接丢弃），也只转发给房间成员。广播与定向都不回显给发送者。
- `LEAVE_ROOM` 成功后服务端回 `LEAVE_SUCCESS`，客户端据此回到大厅。
- `ROOM_LIST` / `LOBBY_UPDATED` 里每个房间带 `inGame` 字段，客户端可按它筛「进行中」的房间；`START_GAME` 之后 `JOIN_ROOM` 会被 `game_already_started` 拒掉。

### Server → Client

    { "type": "ROOM_CREATED", "gameCode": "ABC123" }
    { "type": "JOIN_SUCCESS", "gameCode": "ABC123", "players": ["Host", "Tester1"] }
    { "type": "JOIN_FAILED", "reason": "room_full" }
    { "type": "PLAYER_JOINED", "playerName": "Tester1", "players": ["Host", "Tester1"] }
    { "type": "PLAYER_LEFT", "playerName": "Tester1", "players": ["Host"] }
    { "type": "PLAYER_DISCONNECTED", "playerName": "Tester1", "players": ["Host", "Tester1"] }
    { "type": "PLAYER_RECONNECTED", "playerName": "Tester1", "players": ["Host", "Tester1"] }
    { "type": "GAME_STARTED" }
    { "type": "LEAVE_SUCCESS" }
    { "type": "GAME_DATA", "from": "Tester1", "data": "<base64>" }

- 房间码由服务端生成：6 位大写字母 + 数字。
- `players[]` 第一个元素恒为当前 Host；Host 离开时 `players[0]` 自动成为新 Host（对局状态不迁移）。
- `JOIN_FAILED.reason` ∈ `room_not_found` / `room_full` / `game_already_started` / `name_taken`。

## 掉线重连与移除

中继**不做时间逻辑**，只认名单：`players[]` 就是名单，名字在名单上就能进来。掉线的人名字仍在名单里（座位保留），直到被移除。

1. 成员掉线 → 中继发 `PLAYER_DISCONNECTED`（他仍在 `players[]`，标记为「掉线」）。
2. 掉线者用**同一个 playerName** 重新 REGISTER + `JOIN_ROOM(房码)` → 中继发现同名保留座位，直接坐回（即便已 `inGame`），回 `JOIN_SUCCESS` +（若已开局）`GAME_STARTED`，并向其他人发 `PLAYER_RECONNECTED`。
3. 移除（谁真走了、什么时候踢）是 **Host 逻辑**：房主发 `KICK { playerName }`，中继把该名字从名单移除、发 `PLAYER_LEFT`（在线者同时被送回大厅）。成员自己 `LEAVE_ROOM` 也一样移除。

> 同名即身份：任何持有 token 的人都能用同一个名字顶替（与共享 token 的安全模型一致，防爬虫不防冒名）。
> 房主掉线后座位同样保留、不自动换人——要不要换房主由 Host 层决定（例如让其他人 KICK 掉旧房主）。

---

## 6. UDP 帧格式（二进制）

所有多字节整数小端序。每帧承载一条消息（中继转发的单位），大消息分片、共用同一个序号、重组后再投递。

    字节 0       magic = 0xE9
    字节 1       version = 0x01
    字节 2       flags：bit0=RELIABLE  bit1=FRAG_FIRST  bit2=FRAG_LAST  bit3=ACK_ONLY
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

---

## 7. 对局载荷约定（应用层，中继不参与）

`GAME_DATA.data` 里放什么由游戏自定。推荐沿用 PokerRush 的形态：

    data = base64( utf8( {"v": 协议版本, "t": 消息标签, "p": 消息载荷} ) )

中继不校验 `v`、不认识 `t`，版本不符由收件端自己丢包。

落回传输层的三条约定：

1. `playerId` 就是注册名（`playerName`），否则 Host 的定向 `to` 填不对人。
2. Host 是 `players[0]`，房间内谁跑权威引擎由本协议决定，应用层不再另行选举。
3. WebSocket 恒可靠有序，没有 MTU 问题；UDP 下大消息走 Reliable，高频状态走 Unreliable。
