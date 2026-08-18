// easymulti.js — a minimal EasyMulti WebSocket client.
// Runs unchanged in the browser and in Node (both expose a global WebSocket).
// It speaks the relay protocol (lobby / rooms / GAME_DATA) directly.
//
// 凭证随连接请求一起走：打包进 Sec-WebSocket-Protocol 子协议名（浏览器的 WebSocket
// 设不了自定义头，但设得了子协议）。中继在升级握手里验完才放行 —— 没有「连上了再注册」
// 这一步，验不过的连接压根不会建立。

(function (root, factory) {
  if (typeof module !== "undefined" && module.exports) module.exports = factory();
  else root.EasyMulti = factory();
})(typeof self !== "undefined" ? self : this, function () {
  // 凭证 → 一个合法的子协议名。子协议名的字符集受 RFC 6455 限制（playerId 允许中文和
  // 空格，直接放会非法），所以整包 base64url 编码。与 C# 侧的 RelayHandshake 一一对应。
  function credentialProtocol(token, gameId, playerId) {
    const json = JSON.stringify({ token, gameId, playerId, type: "REGISTER" });
    const bytes = new TextEncoder().encode(json);
    let binary = "";
    for (const b of bytes) binary += String.fromCharCode(b);
    const b64 = btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    return "em." + b64;
  }

  class EasyMultiClient {
    constructor(options) {
      this.url = options.url;
      this.token = options.token;
      this.gameId = options.gameId;
      this.playerId = options.playerId;
      this.state = "disconnected";
      this.roomPlayers = [];   // 玩家名单；host 是专门的连接，不在其中
      this.rooms = [];
      this.gameCode = null;
      this.hostId = null;    // 当前房间的房主连接名（JOIN_SUCCESS 给）
      this._ws = null;
      this._open = false;

      // Callbacks the app sets.
      this.onRegistered = null;       // ()
      this.onRoomList = null;         // (rooms[])
      this.onRoomCreated = null;      // (gameCode)
      this.onRoomJoined = null;       // (gameCode)
      this.onPlayersChanged = null;   // (players[])
      this.onGameData = null;         // (from, data)
      this.onFailed = null;           // (reason)
      this.onClosed = null;           // ()
    }

    connect() {
      if (this._ws) throw new Error("already connected");
      this.state = "connecting";
      this._ws = new WebSocket(this.url, ["easymulti", credentialProtocol(this.token, this.gameId, this.playerId)]);
      this._ws.binaryType = "arraybuffer"; // 对局数据走二进制帧
      this._ws.onopen = () => {
        // 升级成功 = 凭证已经过了。等中继的 REGISTER_SUCCESS 确认身份落定。
        this._open = true;
        this.state = "unregistered";
      };
      this._ws.onmessage = (ev) => {
        if (ev.data instanceof ArrayBuffer) this._onGameFrame(new Uint8Array(ev.data));
        else this._onMessage(String(ev.data));
      };
      this._ws.onerror = () => {};
      this._ws.onclose = () => {
        this._open = false;
        this.state = "disconnected";
        if (this.onClosed) this.onClosed();
      };
    }

    close() { if (this._ws) this._ws.close(); }

    createRoom(roomName, maxPlayers, dedicated) { this._send({ type: "CREATE_ROOM", roomName, maxPlayers, dedicated }); }
    joinRoom(gameCode) { this._send({ type: "JOIN_ROOM", gameCode }); }
    leaveRoom() { this._send({ type: "LEAVE_ROOM" }); }
    listRooms() { this._send({ type: "LIST_ROOMS" }); }
    startGame() { this._send({ type: "START_GAME" }); }
    // 对局数据：二进制帧 [2B 小端 id 长度][id UTF8][payload]。payload 是黑盒，
    // 中继零解析零膨胀直通；data 可传 string（自动 UTF8）或 Uint8Array/ArrayBuffer。
    sendGameData(data, to) {
      if (!this._open) throw new Error("not connected");
      const payload = typeof data === "string" ? new TextEncoder().encode(data)
        : data instanceof ArrayBuffer ? new Uint8Array(data) : data;
      const idBytes = new TextEncoder().encode(to || "");
      const frame = new Uint8Array(2 + idBytes.length + payload.length);
      frame[0] = idBytes.length & 0xff;
      frame[1] = (idBytes.length >> 8) & 0xff;
      frame.set(idBytes, 2);
      frame.set(payload, 2 + idBytes.length);
      this._ws.send(frame);
    }

    _onGameFrame(frame) {
      if (frame.length < 2) return;
      const idLen = frame[0] | (frame[1] << 8);
      if (2 + idLen > frame.length) return;
      const from = new TextDecoder().decode(frame.subarray(2, 2 + idLen));
      if (this.onGameData) this.onGameData(from, frame.subarray(2 + idLen)); // (fromId, Uint8Array)
    }

    _send(obj) {
      if (!this._open) throw new Error("not connected");
      this._ws.send(JSON.stringify(obj));
    }

    _onMessage(text) {
      let msg;
      try { msg = JSON.parse(text); } catch { return; }
      switch (msg.type) {
        case "REGISTER_SUCCESS":
          this.state = "lobby";
          if (this.onRegistered) this.onRegistered();
          break;
        case "REGISTER_FAILED":
          if (this.onFailed) this.onFailed("register:" + msg.reason);
          break;
        case "ROOM_LIST":
          this.rooms = msg.rooms || [];
          if (this.onRoomList) this.onRoomList(this.rooms);
          break;
        case "ROOM_CREATED":
          this.gameCode = msg.gameCode;
          this.state = "inRoom";
          this.roomPlayers = [];             // 开房的就是 host，不算玩家
          this.hostId = this.playerId;
          if (this.onRoomCreated) this.onRoomCreated(msg.gameCode);
          break;
        case "JOIN_SUCCESS":
          this.gameCode = msg.gameCode;
          this.state = "inRoom";
          this.roomPlayers = msg.players;
          this.hostId = msg.hostId;
          if (this.onRoomJoined) this.onRoomJoined(msg.gameCode);
          break;
        case "JOIN_FAILED":
          if (this.onFailed) this.onFailed("join:" + msg.reason);
          break;
        case "PLAYER_JOINED":
        case "PLAYER_LEFT":
          this.roomPlayers = msg.players;
          if (this.onPlayersChanged) this.onPlayersChanged(this.roomPlayers);
          break;
        case "GAME_STARTED":
          this.state = "inGame";
          break;
      }
    }
  }

  return { EasyMultiClient };
});
