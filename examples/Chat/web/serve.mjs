// serve.mjs -- serve the chat web client over plain HTTP.
// For wss (https + WebSocket over TLS), use Caddyfile instead (see README).
//
//   node serve.mjs [--port 8080]
//
// Then open (browser connects to the relay directly over ws://):
//   http://localhost:8080/?url=ws://127.0.0.1:7777/&token=demo-token&room=CODE
// or create a room:
//   http://localhost:8080/?url=ws://127.0.0.1:7777/&token=demo-token&host=1

import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const portIdx = process.argv.indexOf("--port");
const port = portIdx >= 0 ? Number(process.argv[portIdx + 1]) : 8080;

const MIME = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css", ".json": "application/json", ".svg": "image/svg+xml", ".png": "image/png" };

http.createServer((req, res) => {
  let p = req.url.split("?")[0];
  if (p === "/") p = "/index.html";
  const file = path.join(__dirname, path.normalize(p));
  if (!file.startsWith(__dirname)) { res.writeHead(403); res.end("forbidden"); return; }
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404); res.end("not found: " + p); return; }
    res.writeHead(200, { "content-type": MIME[path.extname(file)] || "application/octet-stream" });
    res.end(data);
  });
}).listen(port, () => console.log("serving -> http://localhost:" + port + "/"));
