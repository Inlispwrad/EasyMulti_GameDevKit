# 部署你自己的 EasyMulti 中继

**[English](en.md) · 中文 · [日本語](ja.md)**

这份文档假设你**从来没有部署过服务器**。每一步都会说清楚：这条命令在干什么、正常应该看到什么、看到别的怎么办。全程大约 30 分钟。

---

## 你需要准备什么

| | 说明 |
|---|---|
| 一台 Linux 服务器 | 任何云都行（阿里云、腾讯云、AWS、DigitalOcean…）。最低 1 核 1G 就够 —— 中继很轻，你不会在这台机器上编译任何东西 |
| 它的**公网 IP** | 云控制台的实例详情里能看到 |
| 登录方式 | 买机器时设的 root 密码，或者你上传的 SSH 密钥 |

**不需要**域名，**不需要**懂 Docker，**不需要**在服务器上装 .NET。域名只有在你要让**浏览器**玩家连进来时才需要（见[最后一节](#可选让浏览器也能连httpswss)）。

---

## 先搞清楚你在装什么

中继就是**一个进程**，做三件事：认人、管房间、转发消息。它：

- 监听**同一个端口号的两条通道**：TCP（给网页端的 WebSocket）和 UDP（给桌面／主机端）。默认都是 `7777`。TCP 和 UDP 是两套独立的协议栈，可以共用同一个号码。
- **不存任何数据**。没有数据库，不落盘，重启就把所有房间清空。所以它坏了你重启就行，不会丢东西。
- **不跑游戏逻辑**。谁当房主、谁算伤害，都是你自己游戏里的事。

所以整件事的全貌就是：**把这个进程跑起来 → 开两个端口 → 给它一个密码**。就这三件。

---

## 第 1 步：连上你的服务器

SSH 是远程登录 Linux 服务器的标准方式。你在自己电脑上敲命令，实际在服务器上执行。

**Windows**（PowerShell 或 CMD，Win10 以上自带）、**macOS／Linux**（终端）都是同一条：

```bash
ssh root@你的公网IP
```

如果云厂商给的用户名不是 `root`（阿里云某些镜像是 `admin`，AWS 常见 `ubuntu` 或 `ec2-user`），把 `root` 换掉。

第一次连会问 `Are you sure you want to continue connecting?` —— 输 `yes` 回车。然后输密码（**输密码时屏幕不会有任何显示，这是正常的**，输完直接回车）。

看到类似 `root@iZxxxxx:~#` 的提示符，就说明你已经在服务器上了。之后所有命令都在这里敲。

> **连不上？** 先确认 IP 没抄错；再确认云控制台的防火墙放行了 **22 端口**（几乎所有云默认都放行）。密码错太多次可能被临时锁，等几分钟。

---

## 第 2 步：装 Docker

Docker 让你不用在服务器上装 .NET、不用编译、不用管依赖 —— 我们已经把中继打包成一个镜像，你只要把它跑起来。

```bash
curl -fsSL https://get.docker.com | sh
```

这是 Docker 官方的安装脚本，会自动识别你的系统。跑完大概一两分钟。验证：

```bash
docker --version
```

看到 `Docker version 2x.x.x, build ...` 就成了。

> **`curl: command not found`？** 先装它：Ubuntu／Debian 用 `apt update && apt install -y curl`，CentOS／阿里云 Linux 用 `yum install -y curl`。
>
> **提示 permission denied？** 你不是 root。在命令前加 `sudo`，或者先 `sudo -i` 切到 root。

---

## 第 3 步：生成一个 token

token 是这台中继的**门禁密码**。**它是你自己定的**，服务器不会发给你 —— 你生成一个随机串，一份给中继，一份填进游戏客户端，两边对上才能连。

```bash
openssl rand -hex 32
```

会打印一串 64 位的十六进制字符。**把它复制下来存好**，第 4 步和第 8 步要用同一个。

> **服务器上没有 `openssl`？** 用这条，任何 Linux 都有：
>
> ```bash
> head -c 32 /dev/urandom | od -An -tx1 | tr -d " \n"; echo
> ```

⚠️ 这个串是密码。别提交进 git 仓库，别发到公开的地方。

---

## 第 4 步：把中继跑起来

把 `你的token` 换成第 3 步那一串：

```bash
docker run -d --name easymulti --restart unless-stopped -p 7777:7777/tcp -p 7777:7777/udp -e EASYMULTI_TOKEN=你的token ghcr.io/inlispwrad/easymulti-relay:latest
```

**每个参数在干什么**，值得看一眼，以后排错用得上：

| 参数 | 作用 |
|---|---|
| `-d` | 后台运行。不加的话命令会一直占着你的终端 |
| `--name easymulti` | 给容器起个名字，后面 `docker logs easymulti` 这类命令要用 |
| `--restart unless-stopped` | **服务器重启后自动拉起**，进程崩了也自动重启。少了它，机器一重启你的中继就没了 |
| `-p 7777:7777/tcp` | 把服务器的 7777 TCP 端口映射进容器。网页端走这条 |
| `-p 7777:7777/udp` | 同上，但是 UDP。**桌面端走这条，最容易漏** |
| `-e EASYMULTI_TOKEN=...` | 把 token 传进去。**没有它中继会拒绝启动** —— 这是故意的，绝不允许没有密码就裸奔 |
| `ghcr.io/...:latest` | 要跑哪个镜像。第一次执行会自动下载（约 110MB） |

命令执行完会打印一长串十六进制 —— 那是容器 ID，出现它说明**启动指令已经下达**。但这不等于它跑起来了，下一步验证。

---

## 第 5 步：确认它真的活着

分三层看，哪层断了就知道问题在哪。

### ① 容器在不在跑

```bash
docker ps
```

应该看到一行，`STATUS` 列是 `Up X seconds`。

> 如果**列表是空的**，说明容器启动后立刻退出了。别慌，下一条命令会告诉你原因。

### ② 它说了什么

```bash
docker logs easymulti
```

正常的输出长这样：

```
[EasyMulti] 启动：token=已配置
[EasyMulti] WebSocket: 端口 7777
[EasyMulti] UDP:        端口 7777
[EasyMulti] WebSocket listening on ws://+:7777/
[EasyMulti] UDP listening on udp://0.0.0.0:7777
```

关键是最后两行 —— 两条通道都在监听。

> **看到「缺少 token」？** 第 4 步的 `-e EASYMULTI_TOKEN=` 没填对，或者粘贴时被截断了。`docker rm -f easymulti` 删掉重来。

### ③ 服务器自己能不能连上它

```bash
curl http://localhost:7777/health
```

输出 `ok` 就对了。

到这里，**中继本身没有问题**。但外面还进不来 —— 因为云防火墙默认是关着的。

---

## 第 6 步：开放端口（最容易卡住的一步）

这里有个很多人踩的坑：**有两层防火墙**。

| | 在哪 | 要不要管 |
|---|---|---|
| **云厂商防火墙** | 云控制台的网页上 | **要管**，默认只放行 22 等少数端口 |
| 系统防火墙（ufw／firewalld） | 服务器里面 | 通常**不用管** —— Docker 发布端口时会自己写 iptables 规则，一般直接穿过去 |

所以：**别在服务器里面找防火墙，去云控制台的网页上找。**

要放行两条，**端口号一样但协议不同**：

| 协议 | 端口 | 给谁用 |
|---|---|---|
| **TCP** | 7777 | 网页／WebSocket 客户端，也用来做健康检查 |
| **UDP** | 7777 | Godot／Unity 等桌面客户端 |

**UDP 那条最容易漏。** 漏了的症状很有迷惑性：网页端能连、健康检查也正常，就是桌面端死活连不上。

各家云控制台的位置：

- **阿里云 轻量应用服务器**：控制台 → 点进实例 → **防火墙** 标签 → 添加规则 → 应用类型选「自定义」，分别加 TCP／7777 和 UDP／7777。别选「全部TCP+UDP」那种模板，那会把所有端口都打开。
- **阿里云 ECS ／ 腾讯云 CVM ／ AWS EC2**：找**安全组**（不叫「防火墙」）→ 配置规则 → **入方向** → 添加。端口范围写 `7777/7777`，授权对象 `0.0.0.0/0`。
- **其它云**：找「安全组」「防火墙」「网络 ACL」「入站规则」这类字眼，逻辑都一样。

规则通常立即生效，不需要重启服务器。

---

## 第 7 步：从外面验证（两条腿分别验）

回到**你自己的电脑**（不是服务器），把 IP 换成你的。

### 验 TCP

```bash
curl http://你的公网IP:7777/health
```

输出 `ok` = TCP 通了。

> **超时／连不上？** 就是第 6 步的 TCP 规则没生效。回去检查：端口号对不对、协议选的是不是 TCP、规则是不是「入方向」、有没有点保存。

### 验 UDP

UDP 是无连接协议，**curl 验不了，也没有健康检查**。用仓库里的 Echo 示例去打一发（需要你本机有 [.NET 8 SDK](https://dotnet.microsoft.com/download)）：

```bash
git clone https://github.com/Inlispwrad/EasyMulti_GameDevKit.git
cd EasyMulti_GameDevKit
dotnet run --project examples/Echo -c Release -- --mode host --name Probe --transport udp --relay-host 你的公网IP --token 你的token
```

看到 `房间已创建，房码 = XXXXXX` 就说明 **UDP 也通了**，而且 token 是对的、转发也正常。`Ctrl+C` 退出。

> **卡住不动／没有房码？** UDP 规则没生效，或者 token 填错了。token 错会打印 `bad_token`。

两条都通了，**服务器这边就全部完成了**。

---

## 第 8 步：把游戏连上来

在你的游戏工程里（SDK 怎么接见 [USAGE.md](../USAGE.md)）：

```csharp
EasyMulti.Init(new()
{
    Token     = "第 3 步生成的那个串",
    GameId    = "my-game",          // 你的游戏名，随便起；不同 gameId 的房间互相看不见
    RelayHost = "你的公网IP",
    RelayPort = 7777,
    Codec     = new MemoryPackCodec(),
});
```

三个值必须和服务器对上：**token 一模一样**、IP 是服务器的公网 IP、端口 7777。

跑起来，一个客户端开房、另一个用房码加入，能互发消息就说明整条链路通了。

---

## 可选：让浏览器也能连（HTTPS/wss）

**只有当你要做网页版**（或 Godot／Unity 的 Web 导出）时才需要这一节。桌面端和手机端不需要。

原因是浏览器的安全规则：HTTPS 页面**只允许**连 `wss://`（加密的 WebSocket），明文 `ws://` 会被直接拦掉。而签发证书需要**域名** —— Let's Encrypt 不给纯 IP 签证书，所以这一步绕不过域名。

1. **买一个域名**，加一条 **A 记录**指向你服务器的公网 IP（比如 `relay.你的域名.com`）
2. 云控制台防火墙**再放行 80/TCP 和 443/TCP**。80 是签证书用的，只开 443 签不下来
3. 在服务器上准备三个文件：

```bash
mkdir -p ~/easymulti && cd ~/easymulti
curl -O https://raw.githubusercontent.com/Inlispwrad/EasyMulti_GameDevKit/main/deploy/docker-compose.yml
curl -O https://raw.githubusercontent.com/Inlispwrad/EasyMulti_GameDevKit/main/deploy/Caddyfile
printf 'EASYMULTI_TOKEN=你的token\nEASYMULTI_TAG=latest\n' > .env
```

4. 把 `Caddyfile` 里的 `yourgame.example.com` 换成你的域名：

```bash
sed -i 's/yourgame.example.com/relay.你的域名.com/' Caddyfile
```

5. 停掉第 4 步那个容器，换成这一套（Caddy 会自动申请并续期证书）：

```bash
docker rm -f easymulti
docker compose up -d
```

6. 客户端改成：

```csharp
RelayHost = "relay.你的域名.com",
RelayPort = 443,
Transport = EasyMultiTransport.Wss,
```

**有一件事要注意**：TLS 只保护 WebSocket 这条腿。UDP 走不了 HTTP 反向代理，桌面玩家仍然是明文直连 7777/udp。同一个房间里可以既有加密的网页玩家、又有明文的桌面玩家 —— 这不是故障，但别把「配了 wss」理解成全链路加密。

---

## 日常运维

```bash
docker logs -f easymulti        # 实时看日志（Ctrl+C 退出，不会停掉中继）
docker restart easymulti        # 重启
docker stop easymulti           # 停掉
docker start easymulti          # 再启动
```

**更新到新版本：**

```bash
docker pull ghcr.io/inlispwrad/easymulti-relay:latest
docker rm -f easymulti
# 然后重新执行第 4 步那条 docker run
```

用 compose 那套的话，更新就是两条：

```bash
docker compose pull && docker compose up -d
```

**重启会丢什么？** 所有房间和在线连接。中继无持久化，这是设计如此。玩家客户端会收到断线事件，重连即可。所以尽量别在有人在玩的时候更新。

---

## 排错速查

| 症状 | 原因 | 怎么办 |
|---|---|---|
| `docker: command not found` | Docker 没装上 | 重做第 2 步 |
| `docker ps` 是空的 | 容器启动后就退出了 | `docker logs easymulti` 看原因 |
| 日志说「缺少 token」 | `-e EASYMULTI_TOKEN=` 没填或被截断 | `docker rm -f easymulti` 后重做第 4 步 |
| 服务器上 `curl localhost` 不通 | 中继没起来 | 看第 5 步 |
| 服务器上通、外面不通 | **云防火墙 TCP 规则** | 第 6 步 |
| 网页能连、桌面端连不上 | **云防火墙 UDP 规则** | 第 6 步，注意协议要选 UDP |
| 客户端报 `bad_token` | 两边 token 不一致 | 逐字符核对，注意别多复制了空格或换行 |
| 客户端报 `name_taken` | 同名连接已存在 | 换个玩家名；或等旧连接超时（最多 60 秒）释放 |
| 客户端报 `bad_game_id` | gameId 为空或含非法字符 | 只允许字母数字和 `.` `-` `_`，1–64 字符 |
| 端口被占用 | 7777 被别的程序用了 | 换端口：`-p 8888:7777/tcp -p 8888:7777/udp`，客户端也改成 8888 |
| 服务器重启后中继没了 | 忘了 `--restart unless-stopped` | 重做第 4 步，带上这个参数 |

---

## 安全边界（请务必理解）

**token 只挡爬虫和脚本乱扫，不防有心人。** 这不是免责声明，是这个架构的必然结果：

- token 会被**编进你的游戏客户端**。谁把你的游戏拆包，就能把它抠出来。
- 拿到 token 的人可以连上你的中继、开房、加入房间。
- 中继**不解析你的对局数据**，所以对局内容的安全完全由你自己的游戏协议负责 —— 该加密的加密，权威判定放在房主那边，别信客户端。

中继本身不存任何数据、不落盘，所以最坏情况是有人蹭你的服务器带宽，不会泄露玩家数据（因为它根本没有）。

如果你要做的东西涉及真实财产或敏感信息，这套共享 token 的模型**不够用**，需要你在游戏层自己加一层真正的身份验证。
