// bench.mjs — latency benchmark for the EasyMulti relay (WebSocket side).
// Reuses the exact easymulti.js the browser runs, so this measures the same code path.
//
//   node bench.mjs --url ws://127.0.0.1:7777/ --name BenchWs --room CODE --count 200
//
// It registers, joins (or creates) a room, auto-pongs incoming pings, sends its own
// pings every --interval ms, and prints RTT stats (min/avg/p95/max) when done.

import easymulti from "./easymulti.js";
const { EasyMultiClient } = easymulti;

const args = process.argv.slice(2);
function flag(name, dflt) {
  const i = args.indexOf("--" + name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : dflt;
}

const url = flag("url", "ws://127.0.0.1:7777/");
const token = flag("token", "demo-token");
const game = flag("game", "chat");
const name = flag("name", "BenchWs");
const room = flag("room", null);
const host = flag("host", null) !== null;
const count = parseInt(flag("count", "200"), 10);
const interval = parseInt(flag("interval", "20"), 10);

const client = new EasyMultiClient({ url, token, gameId: game, playerId: name });
const rtts = [];
let pingId = 0;
let pongCount = 0;
const start = Date.now();

client.onGameData = (from, data) => {
  let m; try { m = JSON.parse(new TextDecoder().decode(data)); } catch { return; }
  if (m.t === "ping") {
    client.sendGameData(JSON.stringify({ t: "pong", id: m.id, sent: m.sent }), from);
  } else if (m.t === "pong") {
    rtts.push(Date.now() - m.sent);
    if (++pongCount >= count) finish();
  }
};
client.onFailed = (r) => { console.error("FAILED: " + r); process.exit(1); };

client.onRegistered = () => {
  if (host) client.createRoom("Bench", 8);
  else client.joinRoom(room);
};
client.onRoomCreated = (code) => { console.log("ROOM_CODE=" + code); startPinging(); };
client.onRoomJoined = () => { console.log("joined " + room); startPinging(); };

function startPinging() {
  setInterval(() => {
    client.sendGameData(JSON.stringify({ t: "ping", id: ++pingId, sent: Date.now() }));
  }, interval);
}

function report() {
  rtts.sort((a, b) => a - b);
  const n = rtts.length;
  if (n === 0) { console.error("no samples"); return; }
  const avg = rtts.reduce((a, b) => a + b, 0) / n;
  const p95 = rtts[Math.floor(n * 0.95)];
  console.log(JSON.stringify({
    transport: "ws",
    samples: n,
    min: rtts[0],
    avg: Math.round(avg * 10) / 10,
    p50: rtts[Math.floor(n * 0.5)],
    p95,
    max: rtts[n - 1],
    elapsedMs: Date.now() - start,
  }, null, 2));
}
function finish() { report(); process.exit(0); }

client.connect();
setTimeout(() => { console.error("timeout: got " + pongCount + "/" + count + " pongs"); report(); process.exit(2); }, 30000);
