# 部署 EasyMulti

目标：部署一台中继，之后基本不用再管它。

> **第一次部署、或者不熟悉服务器运维？** 先看手把手的[部署教程](setup/zh.md)
> （[English](setup/en.md) · [日本語](setup/ja.md)）——从 SSH 连接、装 Docker 一路讲到防火墙和排错。
> 本文是**参考手册**：配置项、各种部署形态、边界与取舍。

## 配置来源（优先级从低到高）

1. JSON 配置文件（默认 `easyrelay.config.json`，可用 `--config <路径>` 指定）
2. 环境变量
3. 命令行参数

| 项 | 配置文件字段 | 环境变量 | 命令行 | 默认 |
|---|---|---|---|---|
| 共享 token | `token` | `EASYMULTI_TOKEN` | `--token` | （必填，缺了拒绝启动） |
| WebSocket 开关 | `webSocket.enabled` | `EASYMULTI_WS_ENABLED` | `--no-ws` | true |
| WebSocket 端口 | `webSocket.port` | `EASYMULTI_WS_PORT` | `--ws-port` | 7777 |
| UDP 开关 | `udp.enabled` | `EASYMULTI_UDP_ENABLED` | `--no-udp` | true |
| UDP 端口 | `udp.port` | `EASYMULTI_UDP_PORT` | `--udp-port` | 7777 |
| 最大连接数 | `maxConnections` | `EASYMULTI_MAX_CONNECTIONS` | `--max-connections` | 1000 |
| UDP 空闲超时(ms) | `idleTimeoutMs` | `EASYMULTI_IDLE_TIMEOUT_MS` | — | 60000 |

`--port N` 是便捷写法：同时把 WebSocket 与 UDP 都设为 N（TCP 与 UDP 是不同协议栈，可共用同一端口号）。

示例配置见 [`deploy/easyrelay.config.example.json`](../deploy/easyrelay.config.example.json)。

## 裸机 / VPS

    # 1. 装 .NET 8 SDK
    # 2. 配好 token
    export EASYMULTI_TOKEN=$(openssl rand -hex 32)
    # 3. 跑
    dotnet run --project src/EasyMulti.Relay -c Release

长期运行建议 `dotnet publish -c Release` 后跑 `./easymulti-relay`，或用 systemd 托管。

## Docker（本地 / 内网快跑，明文）

    docker build -t easymulti .
    docker run -d --restart unless-stopped -p 7777:7777/tcp -p 7777:7777/udp -e EASYMULTI_TOKEN=$(openssl rand -hex 32) easymulti

这样跑起来的是明文 `ws://` + UDP，够开发和内网用。**要给浏览器 / WASM 用就得上 wss**，见下一节。

## 生产部署：CI 构建，服务器只拉

**生产机上不编译。** 推代码 → GitHub Actions 跑测试、构建镜像、推到 `ghcr.io` → 服务器
`docker pull`。三个理由：编译最吃 CPU 和内存，不该和线上服务抢；镜像由哪个 commit 出的有据
可查，服务器上现场构建的东西过两个月没人说得清跑的是什么；服务器上只要 Docker，不用装
.NET SDK、不用留源码。

顺带是空间账 —— 服务器现场构建要拉 SDK 镜像（≈1GB）加编译中间产物，好几个 G；
只拉最终镜像的话，中继 ≈110MB（alpine）+ Caddy ≈50MB，两百来兆就够。

流水线在 `.github/workflows/image.yml`，认证不用配任何 secret（`GITHUB_TOKEN` 是 Actions
自带的）。镜像 tag：`main` 分支出 `latest`，`dev` 分支出 `dev`，每次构建另外带一个 commit
短哈希的 tag —— 要回滚就指那个。

### 第一次

**先把镜像设为公开**，否则服务器拉不动：GitHub 仓库页 → 右侧 Packages → `easymulti-relay`
→ Package settings → Change visibility → Public。新推上去的包默认是私有的。

（不想公开也行，那就在服务器上 `docker login ghcr.io`，用一个只带 `read:packages` 权限的
personal access token。中继镜像里没有任何机密，公开更省事。）

然后在服务器上：

    mkdir easymulti && cd easymulti
    # 只要这三个文件，不用 clone 整个仓库
    curl -O https://raw.githubusercontent.com/Inlispwrad/EasyMulti_GameDevKit/main/deploy/docker-compose.yml
    curl -O https://raw.githubusercontent.com/Inlispwrad/EasyMulti_GameDevKit/main/deploy/Caddyfile
    curl -o .env https://raw.githubusercontent.com/Inlispwrad/EasyMulti_GameDevKit/main/deploy/.env.example

    vi .env         # 填 token（openssl rand -hex 32）；在 dev 上迭代就把 EASYMULTI_TAG 改成 dev
    vi Caddyfile    # 换成你的域名（A 记录要先指到这台机器的公网 IP）

    docker compose up -d

### 以后每次更新

    docker compose pull && docker compose up -d

就这两条。`pull` 只下载变化的层，通常几 MB。

端口分工是这套编排里最要紧的一件事，两条腿走的路不一样：

| | 公网入口 | 加密 | 为什么 |
|---|---|---|---|
| WebSocket | Caddy 的 **443**（`wss://`） | 有 | 浏览器 / WASM 的 HTTPS 页面只能连 wss |
| UDP | 中继的 **7777/udp**，直连 | **无** | UDP 走不了 HTTP 反代 |

所以 compose 里**故意没有**把 `7777/tcp` 发布到宿主机 —— 发布了就等于在 443 旁边开了一条
绕过 TLS 的明文口子。中继的 WS 只对 compose 内网可见，公网只能从 Caddy 进。

两个容易踩的点：

- `caddy_data` 这个卷不能省。证书存在里面，少了它每次 `docker compose up` 都会重新签，
  很快会撞上 Let's Encrypt 的签发频率限制，然后你就没证书可用了。
- **80 端口也要放行**，不是只开 443。Let's Encrypt 的 HTTP 校验走 80。

## 阿里云轻量应用服务器

轻量应用服务器（SWAS）跟 ECS 不一样，**它有自己的「防火墙」面板，不是安全组**。默认只放行
22 / 80 / 443 一类，中继要的端口得自己加。具体入口以控制台为准，但要点是固定的：

1. **UDP 规则必须单独加，加的时候协议要选 UDP。** 这是最容易踩的一个：TCP 那条加完，
   网页端连上了，就以为好了；桌面端走 UDP 静默不通，查半天。症状就是「浏览器能玩、Godot/Unity 连不上」。
2. **别在系统里找防火墙。** Docker 发布端口时会自己写 iptables 规则，通常直接穿过系统里的
   ufw / firewalld。真正拦住你的是云控制台那一层，改系统防火墙没用。
3. 上 wss 的话，80 和 443 都要放行（理由见上一节）。

**带宽比 CPU 先撞墙。** 轻量服务器的带宽是套餐峰值（常见 3/4/5 Mbps），而中继是纯转发：
host 广播一条消息给 N 个玩家，出站就是 N 份。3 Mbps ≈ 375 KB/s 总出站，先按这个数算一下
你的消息大小 × 频率 × 人数，再决定套餐——多半是它决定你能撑多少人，而不是 `MaxConnections`。
套餐还带月流量包，跑超了会被限速。

## 健康检查

中继在 WebSocket 端口上应答 `GET /health` → `200 ok`。云平台（Cloud Run / Render / Railway / K8s / ALB）的存活探针直接指过去即可：

    curl http://你的服务器:7777/health     # -> ok

其它非 WebSocket 升级的请求一律 `426 Upgrade Required`。

> UDP 端口没有健康检查——UDP 无连接，探不了。如果平台要求所有端口都可探，就把探针指向 WebSocket 端口。

## 反向代理（wss://）

HTTPS 页面只能连 `wss://` —— 浏览器 JS、以及 Godot / Unity 的 **WASM 导出用 C# 连**时都一样，
明文会被混合内容策略拦掉。中继自身只跑明文 `ws://`，在它前面放一个反向代理终结 TLS：

    # caddy 例子
    yourgame.example.com {
        reverse_proxy 127.0.0.1:7777
    }

**反代必须透传 `Sec-WebSocket-Protocol` 头** —— 连接凭证就装在里面（见
[PROTOCOL.md §3](PROTOCOL.md)）。caddy 的 `reverse_proxy` 默认透传；nginx 配 WebSocket
升级时别把它漏掉，否则所有 WebSocket 连接都会以「没有可解析的凭证」被中继挡在门外。

客户端侧就是 `Init` 的传输开关，端口填反代的 443：

    EasyMulti.Init(new()
    {
        Token = token, GameId = gameId,
        RelayHost = "yourgame.example.com",
        RelayPort = 443,
        Transport = EasyMultiTransport.Wss,
        Path      = "/em",   // 中继挂在子路径下才需要，默认根路径
    });

**证书必须是真的**（Caddy 自动签的 Let's Encrypt 就行）。SDK 的目标框架 netstandard2.1 里，
`ClientWebSocketOptions` 没有跳过证书校验的口子（`RemoteCertificateValidationCallback` 是
.NET 5 才加的），所以自签证书连不上，也没法在代码里绕过去。

### 加密只覆盖 WebSocket 那条腿

UDP 走不了 HTTP 反代，客户端直连中继的 UDP 端口，**明文**（需在防火墙放行）。所以同一间房里
可能是：浏览器玩家 wss/443 加密，桌面玩家 UDP/7777 明文。两边照样一起玩（中继按房间路由，
不看谁从哪条管子进来），但别把「配了 wss」理解成全链加密 —— 对局内容的机密性仍归你自己的
对局协议管，见下面的安全边界。

> 选平台前先确认它给不给裸 UDP 端口：不少 PaaS（Cloud Run / Railway / Render 之类）只放
> HTTP(S)，落在那种平台上 UDP 通道直接不可用，全员只能走 ws / wss。

## 安全边界（说清楚，别误会）

- token 是共享密钥，**知道它的人都能连**。它只挡爬虫和脚本乱扫，**不防专业人工黑客**。不要把敏感数据的安全押在它上面。
- 中继不解析 `GAME_DATA.data`，对局内容的安全由你自己的对局协议负责（该加密加密、该签名签名）。
- 中继无持久化、无日志落盘（日志走 stdout），重启清空所有房间。
- 建议把中继放在只有游戏客户端能访问的网络里，或至少限制来源 IP。
