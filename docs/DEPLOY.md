# 部署 EasyMulti

目标：部署一台中继，之后基本不用再管它。

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

示例配置见 `easyrelay.config.example.json`。

## 裸机 / VPS

    # 1. 装 .NET 8 SDK
    # 2. 配好 token
    export EASYMULTI_TOKEN=$(openssl rand -hex 32)
    # 3. 跑
    dotnet run --project src/EasyMulti.Relay -c Release

长期运行建议 `dotnet publish -c Release` 后跑 `./easymulti-relay`，或用 systemd 托管。

## Docker

    docker build -t easymulti .
    docker run -d --restart unless-stopped -p 7777:7777/tcp -p 7777:7777/udp -e EASYMULTI_TOKEN=$(openssl rand -hex 32) easymulti

## 反向代理（wss://）

浏览器里的 HTTPS 页面只能连 `wss://`，而中继只跑明文 `ws://`。在它前面放一个反向代理终结 TLS：

    # caddy 例子
    yourgame.example.com {
        reverse_proxy 127.0.0.1:7777
    }

UDP 不走反代，客户端直连中继的 UDP 端口即可（需在防火墙放行）。

## 安全边界（说清楚，别误会）

- token 是共享密钥，**知道它的人都能连**。它只挡爬虫和脚本乱扫，**不防专业人工黑客**。不要把敏感数据的安全押在它上面。
- 中继不解析 `GAME_DATA.data`，对局内容的安全由你自己的对局协议负责（该加密加密、该签名签名）。
- 中继无持久化、无日志落盘（日志走 stdout），重启清空所有房间。
- 建议把中继放在只有游戏客户端能访问的网络里，或至少限制来源 IP。
