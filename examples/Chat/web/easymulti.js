// easymulti.js — a minimal EasyMulti WebSocket client.
// Runs unchanged in the browser and in Node (both expose a global WebSocket).
// It speaks the relay protocol (REGISTER / lobby / rooms / GAME_DATA) directly.

(function (root, factory) {
  if (typeof module !== "undefined" && module.exports) module.exports = factory();
  else root.EasyMulti = factory();
})(typeof self !== "undefined" ? self : this, function () {
  class EasyMultiClient {
    constructor(options) {
      this.url = options.url;
      this.token = options.token;
      this.gameId = options.gameId;
      this.playerName = options.playerName;
      this.state = "disconnected";
      this.roomPlayers = [];
      this.rooms = [];
      this.gameCode = null;
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
      this._ws = new WebSocket(this.url);
      this._ws.onopen = () => {
        this._open = true;
        this.state = "unregistered";
        this._send({ type: "REGISTER", token: this.token, gameId: this.gameId, playerName: this.playerName });
      };
      this._ws.onmessage = (ev) => this._onMessage(String(ev.data));
      this._ws.onerror = () => {};
      this._ws.onclose = () => {
        this._open = false;
        this.state = "disconnected";
        if (this.onClosed) this.onClosed();
      };
    }

    close() { if (this._ws) this._ws.close(); }

    createRoom(roomName, maxPlayers) { this._send({ type: "CREATE_ROOM", roomName, maxPlayers }); }
    joinRoom(gameCode) { this._send({ type: "JOIN_ROOM", gameCode }); }
    leaveRoom() { this._send({ type: "LEAVE_ROOM" }); }
    listRooms() { this._send({ type: "LIST_ROOMS" }); }
    startGame() { this._send({ type: "START_GAME" }); }
    sendGameData(data, to) { this._send({ type: "GAME_DATA", data, to }); }

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
        case "LOBBY_UPDATED":
          this.rooms = msg.rooms || [];
          if (this.onRoomList) this.onRoomList(this.rooms);
          break;
        case "ROOM_CREATED":
          this.gameCode = msg.gameCode;
          this.state = "inRoom";
          this.roomPlayers = [this.playerName];
          if (this.onRoomCreated) this.onRoomCreated(msg.gameCode);
          break;
        case "JOIN_SUCCESS":
          this.gameCode = msg.gameCode;
          this.state = "inRoom";
          this.roomPlayers = msg.players;
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
        case "GAME_DATA":
          if (this.onGameData) this.onGameData(msg.from, msg.data);
          break;
      }
    }
  }

  return { EasyMultiClient };
});
