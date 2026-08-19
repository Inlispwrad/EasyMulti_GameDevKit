using System.Collections.Generic;
using EasyMultiNet;
using Godot;

/// <summary>
/// 玩家侧的全部界面和逻辑。
///
/// <para>
/// 这里只用到 <see cref="EasyMultiClient"/> / <see cref="EasyMultiHost"/> / <see cref="Room"/> 三个门面
/// 类型，没有出现「中继 / 房间协议 / UDP」。
/// </para>
/// <para>
/// 更要紧的两点：
/// </para>
/// <list type="number">
/// <item>
/// <b>玩家的游戏逻辑只有两句</b>：<c>_me.Send(text)</c> 和 <c>_me.Receive&lt;SayMsg&gt;(…)</c>。
/// 房间是怎么建的、谁在管、消息路由给谁，玩家一概不知道也不需要知道。
/// </item>
/// <item>
/// <b>没有任何一句「我是不是房主」的判断。</b> 想自己开房，就先开一个
/// <see cref="EasyMultiHost"/> 跑 <see cref="ChatCore"/>，然后照常用 <c>_me.Join(code)</c>
/// 走进去 —— 和加入别人的房是同一句代码。
/// </item>
/// </list>
/// </summary>
public partial class Main : Control
{
    private const int RoomCapacity = 8;

    /// <summary>大厅自动刷新的间隔。调大就少问、列表旧一点；调小反过来。全在客户端手里。</summary>
    private const double LobbyRefreshSeconds = 3.0;

    private readonly List<string> _lobbyCodes = new List<string>();

    private EasyMultiClient _me;        // 我这个玩家。所有人都有。
    private EasyMultiHost _host;        // 只有「我来开房」时才有。
    private ChatCore _core;    // 只有开房的人才跑权威逻辑。
    private double _refreshIn; // 距离下次主动问大厅还有多久。

    private Control _login;
    private Control _lobby;
    private Control _chat;
    private LineEdit _hostInput;
    private LineEdit _portInput;
    private LineEdit _tokenInput;
    private LineEdit _nameInput;
    private LineEdit _roomNameInput;
    private LineEdit _sayInput;
    private ItemList _roomList;
    private ItemList _playerList;
    private RichTextLabel _log;
    private Label _lobbyHeader;
    private Label _roomHeader;
    private Label _status;

    public override void _Ready()
    {
        BuildUi();
        ShowOnly(_login);
    }

    /// <summary>
    /// 大厅列表是<b>问才有</b>的 —— 中继绝不主动推（1000 人在线时推送会级联，见
    /// RelayServer 的 Room list 一节）。所以「要多新」是客户端自己的决定和开销：
    /// 只在大厅这一屏可见时才问，进了房间就不问了。
    /// </summary>
    public override void _Process(double delta)
    {
        if (_me == null || _lobby == null || !_lobby.Visible) return;

        // 状态自查：不需要一个频道来告诉我连上没有，问一下就知道。
        _lobbyHeader.Text = _me.Connected ? $"大厅 —— 你是 {_me.Id}" : "大厅 —— 连接中…";

        _refreshIn -= delta;
        if (_refreshIn > 0) return;
        _refreshIn = LobbyRefreshSeconds;
        _me.RefreshRooms();
    }

    // ── 玩家：上线 → 进房 → 收发消息 ─────────────────────────────────────

    private void DoGoOnline()
    {
        string host = _hostInput.Text.Trim();
        string token = _tokenInput.Text.Trim();
        string name = _nameInput.Text.Trim();
        if (host.Length == 0) { Status("填一下中继地址"); return; }
        if (token.Length == 0) { Status("填一下 token —— 中继没有默认密码"); return; }
        if (name.Length == 0) { Status("先起个名字"); return; }
        if (!int.TryParse(_portInput.Text.Trim(), out int port) || port <= 0 || port > 65535)
        {
            Status("端口要是 1–65535 之间的数字");
            return;
        }

        // 配置在这一刻才写入：测试工具的凭证由使用者输入，不躺在代码里。
        Net.Configure(host, port, token);
        Net.Remember(host, port, token, name);

        _me = EasyMulti.Client.Connect(name);
        _me.RoomsChanged += RenderLobby;
        _me.Joined       += OnJoined;
        _me.Left         += OnLeft;
        _me.Receive<SayMsg>(OnSay);      // ← 玩家的游戏逻辑,全部在这两条类型通道里
        _me.Receive<WhoMsg>(OnWho);
        _me.Rejected     += Status;
        _me.Disconnected += reason => Status("断线：" + reason);

        _me.RefreshRooms();  // 不用等连上 —— 没连上就先攒着，连上自动补发
        ShowOnly(_lobby);    // 直接进大厅屏；连上没有由下面的自查渲染
        _refreshIn = 0;
    }

    /// <summary>说话。玩家不知道也不需要知道这条消息会被送到哪儿去。</summary>
    private void DoSay()
    {
        string text = _sayInput.Text.Trim();
        if (text.Length == 0) return;
        _me.Send(text); // T=string 一条通道；房主定序后以 SayMsg 广播回来
        _sayInput.Clear();
        // 不在本地抢先显示 —— 等房主定序后广播回来，这样自己看到的顺序和别人完全一致。
    }

    /// <summary>房主广播的定序发言。</summary>
    private void OnSay(SayMsg m) =>
        _log.AppendText($"[color=#888]{m.Seq}[/color]  [b]{Escape(m.Who)}[/b]：{Escape(m.Text)}\n");

    /// <summary>房主广播的玩家名单。</summary>
    private void OnWho(WhoMsg m)
    {
        _playerList.Clear();
        foreach (string name in m.Players)
        {
            _playerList.AddItem(name == _me.Id ? $"{name}（你）" : name);
        }
    }

    // ── 开房 / 加房。两条路的收尾是同一句 _me.Join(code) ─────────────────

    /// <summary>我来开房：先起一个 EasyMultiHost 跑 Core，然后照常用玩家身份走进去。</summary>
    private void DoOpenRoom()
    {
        if (_host != null) return;
        string title = _roomNameInput.Text.Trim();
        if (title.Length == 0) title = $"{_me.Id} 的房间";

        _host = EasyMulti.Host.Open(_me.Id, title, players: RoomCapacity);
        _host.Opened  += code =>
        {
            _core = new ChatCore(_host); // 权威逻辑起来了
            _me.Join(code);              // ↓ 从这里开始,和加入别人的房完全是同一条路
        };
        _host.Rejected     += reason => Status("房主被拒：" + reason);
        _host.Disconnected += reason => Status("房主断线：" + reason);

        Status("正在开房…");
    }

    private void DoJoinSelected()
    {
        int[] picked = _roomList.GetSelectedItems();
        if (picked.Length == 0) { Status("先在列表里选一间房"); return; }
        _me.Join(_lobbyCodes[picked[0]]);
    }

    private void DoLeave()
    {
        _me.Leave();
        if (_host != null) { _host.Close(); _host = null; _core = null; }
    }

    private void OnJoined(string code)
    {
        _log.Clear();
        _playerList.Clear();
        ShowOnly(_chat);
        _roomHeader.Text = _host != null ? $"房间 {code}（这台机器在跑房主）" : $"房间 {code}";
        Status($"已进入房间 {code}");
        _sayInput.GrabFocus();
    }

    private void OnLeft()
    {
        ShowOnly(_lobby);
        _refreshIn = 0; // 立刻问一次，别等下一个周期
    }

    private void RenderLobby(IReadOnlyList<Room> rooms)
    {
        _roomList.Clear();
        _lobbyCodes.Clear();
        foreach (Room r in rooms)
        {
            _lobbyCodes.Add(r.Code);
            _roomList.AddItem($"{r.Name}   [{r.Code}]   {r.Players}/{r.Capacity}   {(r.Started ? "进行中" : "可加入")}");
        }

        if (_lobbyCodes.Count == 0) _roomList.AddItem("（还没有人开房，你可以开一间）", selectable: false);
    }

    private void Status(string text) => _status.Text = text;

    private static string Escape(string text) => text.Replace("[", "[lb]");

    // ── 建界面（纯代码，省一个 .tscn）─────────────────────────────────────

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "left", "top", "right", "bottom" })
        {
            margin.AddThemeConstantOverride("margin_" + side, 16);
        }

        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        var title = new Label { Text = "EasyMulti 聊天室 · Godot 4.7.1" };
        title.AddThemeFontSizeOverride("font_size", 20);
        root.AddChild(title);

        root.AddChild(_login = BuildLogin());
        root.AddChild(_lobby = BuildLobby());
        root.AddChild(_chat = BuildChat());

        _status = new Label { Text = "" };
        _status.AddThemeColorOverride("font_color", new Color(0.6f, 0.7f, 0.8f));
        root.AddChild(_status);
    }

    private Control BuildLogin()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);

        var hint = new Label
        {
            Text = "填你自己那台中继的地址和 token。这是测试工具，凭证由你输入，不写死在代码里。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeColorOverride("font_color", new Color(0.6f, 0.7f, 0.8f));
        box.AddChild(hint);

        (string host, int port, string token, string name) = Net.Recall(); // 上次填过就带回来

        var form = new GridContainer { Columns = 2 };
        form.AddThemeConstantOverride("h_separation", 10);
        form.AddThemeConstantOverride("v_separation", 6);

        _hostInput = AddField(form, "中继地址", host, "域名或 IP，不带 ws:// 前缀");
        _portInput = AddField(form, "端口", port.ToString(), "7777");
        _tokenInput = AddField(form, "token", token, "和服务器上那个一模一样");
        _nameInput = AddField(form, "你的名字", name, "在这个房间里的身份");
        _nameInput.TextSubmitted += _ => DoGoOnline();
        box.AddChild(form);

        var go = new Button { Text = "连接" };
        go.CustomMinimumSize = new Vector2(120, 0);
        go.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        go.Pressed += DoGoOnline;
        box.AddChild(go);
        return box;
    }

    /// <summary>表单的一行：左边标签，右边输入框。</summary>
    private static LineEdit AddField(GridContainer form, string label, string value, string placeholder)
    {
        form.AddChild(new Label { Text = label });
        var input = new LineEdit
        {
            Text = value,
            PlaceholderText = placeholder,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        form.AddChild(input);
        return input;
    }

    private Control BuildLobby()
    {
        var box = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _lobbyHeader = new Label { Text = "大厅" };
        box.AddChild(_lobbyHeader);

        _roomList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _roomList.ItemActivated += index => _me.Join(_lobbyCodes[(int)index]);
        box.AddChild(_roomList);

        var actions = new HBoxContainer();
        _roomNameInput = new LineEdit { PlaceholderText = "房间名", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _roomNameInput.TextSubmitted += _ => DoOpenRoom();
        actions.AddChild(_roomNameInput);

        var open = new Button { Text = "开一间" };
        open.Pressed += DoOpenRoom;
        actions.AddChild(open);

        var join = new Button { Text = "加入选中" };
        join.Pressed += DoJoinSelected;
        actions.AddChild(join);

        var refresh = new Button { Text = "刷新" };
        refresh.Pressed += () => _me.RefreshRooms();
        actions.AddChild(refresh);

        box.AddChild(actions);
        return box;
    }

    private Control BuildChat()
    {
        var box = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };

        // 标题栏：房间名在左，「离开房间」在右。它以前是贴在输入框正下方的通栏按钮 ——
        // 那个位置和体量在任何聊天界面里都是「发送」的位置，很容易误按。
        var head = new HBoxContainer();
        _roomHeader = new Label { Text = "房间", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        head.AddChild(_roomHeader);

        var leave = new Button { Text = "离开房间" };
        leave.Pressed += DoLeave;
        head.AddChild(leave);
        box.AddChild(head);

        var middle = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        middle.AddThemeConstantOverride("separation", 10);
        _log = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        middle.AddChild(_log);

        var side = new VBoxContainer { CustomMinimumSize = new Vector2(180, 0) };
        side.AddChild(new Label { Text = "在场玩家" });
        _playerList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        side.AddChild(_playerList);
        middle.AddChild(side);
        box.AddChild(middle);

        var row = new HBoxContainer();
        _sayInput = new LineEdit { PlaceholderText = "说点什么…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sayInput.TextSubmitted += _ => DoSay();
        row.AddChild(_sayInput);

        var send = new Button { Text = "发送" };
        send.CustomMinimumSize = new Vector2(96, 0); // 底部这一行只有一个动作，就是发送
        send.Pressed += DoSay;
        row.AddChild(send);
        box.AddChild(row);
        return box;
    }

    private void ShowOnly(Control screen)
    {
        _login.Visible = screen == _login;
        _lobby.Visible = screen == _lobby;
        _chat.Visible = screen == _chat;
    }
}
