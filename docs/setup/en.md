# Deploy your own EasyMulti relay

**English · [中文](zh.md) · [日本語](ja.md)**

This guide assumes you have **never deployed a server before**. Every step explains what the command does, what you should see, and what to do if you see something else. Budget about 30 minutes.

---

## What you need

| | |
|---|---|
| A Linux server | Any provider works (AWS, DigitalOcean, Hetzner, Alibaba Cloud, Vultr…). 1 vCPU and 1 GB of RAM is plenty — the relay is small, and you will not compile anything on this machine |
| Its **public IP address** | Shown on the instance page in your provider's console |
| A way to log in | The root password you set when creating the server, or an SSH key you uploaded |

You do **not** need a domain name, you do **not** need to know Docker, and you do **not** need to install .NET on the server. A domain is only required if you want **browser** players to connect — see [the last section](#optional-let-browsers-connect-httpswss).

---

## First, understand what you are installing

The relay is **one process** that does three things: check credentials, manage rooms, forward messages. It:

- Listens on **two channels under the same port number**: TCP (WebSocket, for browsers) and UDP (for desktop and console clients). Both default to `7777`. TCP and UDP are separate protocol stacks, so they can share a number.
- **Stores nothing.** No database, nothing written to disk. A restart clears every room. That means if something goes wrong, restarting costs you nothing but the current sessions.
- **Runs no game logic.** Who hosts, who takes damage, who deals the cards — that all lives in your game.

So the whole job is: **start the process → open two ports → give it a password.** That is all.

---

## Step 1: Connect to your server

SSH is the standard way to log into a Linux server remotely. You type commands on your own computer; they run on the server.

The same command works on **Windows** (PowerShell or CMD, built in since Win10) and on **macOS / Linux** (Terminal):

```bash
ssh root@YOUR_SERVER_IP
```

If your provider gave you a different username (`ubuntu` and `ec2-user` are common on AWS, some Alibaba Cloud images use `admin`), replace `root` with it.

The first connection asks `Are you sure you want to continue connecting?` — type `yes` and press Enter. Then type your password. **Nothing appears on screen while you type a password — that is normal.** Press Enter when done.

When you see a prompt like `root@server:~#`, you are on the server. Every command from here runs there.

> **Cannot connect?** Check the IP first. Then check that your provider's firewall allows **port 22** (almost every provider allows it by default). Too many wrong passwords can lock you out temporarily — wait a few minutes.

---

## Step 2: Install Docker

Docker means you do not install .NET on the server, do not compile anything, and do not manage dependencies. We ship the relay as a prebuilt image; you just run it.

```bash
curl -fsSL https://get.docker.com | sh
```

This is Docker's official installer and detects your distribution automatically. It takes a minute or two. Verify:

```bash
docker --version
```

`Docker version 2x.x.x, build ...` means you are set.

> **`curl: command not found`?** Install it first: `apt update && apt install -y curl` on Ubuntu/Debian, `yum install -y curl` on CentOS/RHEL-family systems.
>
> **Permission denied?** You are not root. Prefix the command with `sudo`, or run `sudo -i` first.

---

## Step 3: Generate a token

The token is the **door key** for your relay. **You choose it** — the server does not hand you one. You generate a random string, give one copy to the relay and put the same copy in your game client. They have to match.

```bash
openssl rand -hex 32
```

This prints 64 hexadecimal characters. **Copy it somewhere safe** — you need the same value in Step 4 and Step 8.

> **No `openssl` on the server?** This works on any Linux:
>
> ```bash
> head -c 32 /dev/urandom | od -An -tx1 | tr -d " \n"; echo
> ```

⚠️ This string is a password. Do not commit it to a git repository and do not post it anywhere public.

---

## Step 4: Start the relay

Replace `YOUR_TOKEN` with the string from Step 3:

```bash
docker run -d --name easymulti --restart unless-stopped -p 7777:7777/tcp -p 7777:7777/udp -e EASYMULTI_TOKEN=YOUR_TOKEN ghcr.io/inlispwrad/easymulti-relay:latest
```

**What each flag does** — worth reading once; it pays off when something breaks:

| Flag | Purpose |
|---|---|
| `-d` | Run in the background. Without it the command holds your terminal |
| `--name easymulti` | Names the container, so later commands like `docker logs easymulti` can refer to it |
| `--restart unless-stopped` | **Starts again after a server reboot**, and restarts if the process crashes. Without this, rebooting the machine silently kills your relay |
| `-p 7777:7777/tcp` | Maps the server's TCP port 7777 into the container. Browser clients use this |
| `-p 7777:7777/udp` | The same for UDP. **Desktop clients use this one, and it is the one people forget** |
| `-e EASYMULTI_TOKEN=...` | Passes the token in. **Without it the relay refuses to start** — deliberately, so it can never run unprotected |
| `ghcr.io/...:latest` | Which image to run. The first run downloads it (about 110 MB) |

The command prints a long hexadecimal string — that is the container ID, and it means the **start command was accepted**. It does not yet mean the relay is running. Verify next.

---

## Step 5: Confirm it is actually alive

Check three layers. Whichever one fails tells you where the problem is.

### 1. Is the container running?

```bash
docker ps
```

You should see one row with `Up X seconds` in the `STATUS` column.

> If the **list is empty**, the container exited right after starting. Do not worry — the next command tells you why.

### 2. What did it say?

```bash
docker logs easymulti
```

A healthy start looks like this:

```
[EasyMulti] Starting: token=configured
[EasyMulti] WebSocket: port 7777
[EasyMulti] UDP:       port 7777
[EasyMulti] WebSocket listening on ws://+:7777/
[EasyMulti] UDP listening on udp://0.0.0.0:7777
```

The last two lines are the ones that matter — both channels are listening.

> **Says the token is missing?** The `-e EASYMULTI_TOKEN=` in Step 4 was empty or got truncated when pasting. Run `docker rm -f easymulti` and redo Step 4.

### 3. Can the server reach it locally?

```bash
curl http://localhost:7777/health
```

It should print `ok`.

At this point **the relay itself is fine**. The outside world still cannot reach it, because your provider's firewall is closed by default.

---

## Step 6: Open the ports (the step people get stuck on)

Here is the trap: **there are two firewalls.**

| | Where | Do you touch it? |
|---|---|---|
| **Your cloud provider's firewall** | In the provider's web console | **Yes.** By default it only allows a few ports such as 22 |
| The OS firewall (ufw / firewalld) | On the server itself | Usually **no.** Docker writes its own iptables rules when publishing ports and normally bypasses it |

So: **do not go looking for a firewall on the server — open the provider's web console.**

You need two rules with the **same port number but different protocols**:

| Protocol | Port | Used by |
|---|---|---|
| **TCP** | 7777 | Browser / WebSocket clients, and the health check |
| **UDP** | 7777 | Desktop clients such as Godot and Unity |

**The UDP rule is the one people miss,** and the symptom is misleading: browsers connect fine, the health check passes, and only desktop clients fail.

Where to find it:

- **AWS EC2 / Alibaba Cloud ECS / Tencent Cloud CVM**: look for **security groups** (not "firewall") → edit inbound rules → add. Port range `7777`, source `0.0.0.0/0`.
- **Alibaba Cloud Simple Application Server**: console → open the instance → **Firewall** tab → add rule → choose "Custom" and add TCP/7777 and UDP/7777 separately. Avoid the "all TCP+UDP" template, which opens every port.
- **DigitalOcean / Hetzner / Vultr**: "Firewalls" or "Networking" in the control panel, then inbound rules.

Rules normally take effect immediately; you do not need to reboot.

---

## Step 7: Verify from outside (test both legs)

Go back to **your own computer** (not the server) and substitute your IP.

### Test TCP

```bash
curl http://YOUR_SERVER_IP:7777/health
```

`ok` means TCP is open.

> **Times out?** The TCP rule from Step 6 is not in effect. Recheck: right port number, protocol set to TCP, rule on the *inbound* direction, and actually saved.

### Test UDP

UDP is connectionless, so **curl cannot test it and there is no health check for it.** Use the Echo example from the repository instead — you need the [.NET 8 SDK](https://dotnet.microsoft.com/download) on your own machine:

```bash
git clone https://github.com/Inlispwrad/EasyMulti_GameDevKit.git
cd EasyMulti_GameDevKit
dotnet run --project examples/Echo -c Release -- --mode host --name Probe --transport udp --relay-host YOUR_SERVER_IP --token YOUR_TOKEN
```

If it prints a room code, **UDP works** — and so do your token and the relay's forwarding path. Press `Ctrl+C` to quit.

> **Hangs with no room code?** Either the UDP rule is not in effect, or the token is wrong. A wrong token prints `bad_token`.

Once both pass, **the server side is done.**

---

## Step 8: Point your game at it

In your game project (see [USAGE.md](../USAGE.md) for how to add the SDK):

```csharp
EasyMulti.Init(new()
{
    Token     = "the string from Step 3",
    GameId    = "my-game",          // your game's namespace; rooms in different gameIds never see each other
    RelayHost = "YOUR_SERVER_IP",
    RelayPort = 7777,
    Codec     = new MemoryPackCodec(),
});
```

Three values have to match the server: the **token character for character**, the server's public IP, and port 7777.

Run two clients, have one create a room and the other join with the room code. Messages flowing both ways means the whole path works.

---

## Optional: let browsers connect (HTTPS/wss)

You only need this section if you are shipping a **web build** (including Godot or Unity web exports). Desktop and mobile do not need it.

The reason is a browser rule: an HTTPS page may **only** open `wss://` (encrypted WebSocket); plain `ws://` is blocked as mixed content. Issuing a certificate requires a **domain name** — Let's Encrypt does not issue certificates for bare IP addresses, so there is no way around buying one.

1. **Buy a domain** and add an **A record** pointing at your server's public IP (for example `relay.yourgame.com`).
2. Open **80/TCP and 443/TCP** in the provider firewall as well. Port 80 is used for certificate validation; opening only 443 will fail.
3. Put three files on the server:

```bash
mkdir -p ~/easymulti && cd ~/easymulti
curl -O https://raw.githubusercontent.com/Inlispwrad/EasyMulti_GameDevKit/main/deploy/docker-compose.yml
curl -O https://raw.githubusercontent.com/Inlispwrad/EasyMulti_GameDevKit/main/deploy/Caddyfile
printf 'EASYMULTI_TOKEN=YOUR_TOKEN\nEASYMULTI_TAG=latest\n' > .env
```

4. Put your domain into the `Caddyfile`:

```bash
sed -i 's/yourgame.example.com/relay.yourgame.com/' Caddyfile
```

5. Replace the container from Step 4 with this stack. Caddy requests and renews the certificate for you:

```bash
docker rm -f easymulti
docker compose up -d
```

6. Update the client:

```csharp
RelayHost = "relay.yourgame.com",
RelayPort = 443,
Transport = EasyMultiTransport.Wss,
```

**One thing to be clear about:** TLS only protects the WebSocket leg. UDP cannot pass through an HTTP reverse proxy, so desktop players still connect in the clear on 7777/udp. A single room can hold encrypted browser players and plaintext desktop players at once — that is not a fault, but do not read "we enabled wss" as "the whole thing is encrypted".

---

## Day-to-day operation

```bash
docker logs -f easymulti        # follow the log live (Ctrl+C leaves the relay running)
docker restart easymulti        # restart
docker stop easymulti           # stop
docker start easymulti          # start again
```

**Updating to a new version:**

```bash
docker pull ghcr.io/inlispwrad/easymulti-relay:latest
docker rm -f easymulti
# then run the docker run command from Step 4 again
```

With the compose stack it is two commands:

```bash
docker compose pull && docker compose up -d
```

**What does a restart cost?** Every room and every live connection. The relay keeps no state on purpose. Clients receive a disconnect event and can reconnect, but try not to update while people are playing.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `docker: command not found` | Docker is not installed | Redo Step 2 |
| `docker ps` shows nothing | The container exited on startup | `docker logs easymulti` to see why |
| Log says the token is missing | `-e EASYMULTI_TOKEN=` was empty or truncated | `docker rm -f easymulti`, redo Step 4 |
| `curl localhost` fails on the server | The relay is not running | See Step 5 |
| Works locally, not from outside | **Provider firewall, TCP rule** | Step 6 |
| Browsers connect, desktop does not | **Provider firewall, UDP rule** | Step 6 — make sure the protocol is UDP |
| Client reports `bad_token` | The two tokens differ | Compare character by character; watch for a stray space or newline |
| Client reports `name_taken` | A connection with that player id already exists | Use a different name, or wait up to 60 seconds for the old one to time out |
| Client reports `bad_game_id` | gameId is empty or has illegal characters | Letters, digits, `.`, `-`, `_` only; 1–64 characters |
| Port already in use | Something else holds 7777 | Use another one: `-p 8888:7777/tcp -p 8888:7777/udp`, and set 8888 in the client |
| Relay gone after a reboot | `--restart unless-stopped` was missing | Redo Step 4 with that flag |

---

## Security boundary (please read)

**The token stops crawlers and casual scanners. It does not stop a determined person.** This is not a disclaimer; it follows from the design:

- The token is **compiled into your game client**. Anyone who unpacks your game can extract it.
- Whoever holds the token can connect to your relay, create rooms and join rooms.
- The relay **never parses your gameplay data**, so the safety of that data is entirely your game protocol's job — encrypt what needs encrypting, and keep authoritative decisions on the host rather than trusting clients.

The relay itself stores nothing and writes nothing to disk, so the worst case is someone using your bandwidth. There is no player data to leak, because none is kept.

If what you are building involves real money or sensitive information, this shared-token model is **not enough** — add real authentication in your own game layer.
