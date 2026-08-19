using System.Collections.Generic;
using EasyMultiNet;
using Godot;

/// <summary>
/// 自动化验收用的驱动器，不是示例的一部分。它走的是和界面完全相同的那两扇门
/// （<see cref="EasyMultiClient"/> / <see cref="EasyMultiHost"/> + <see cref="ChatCore"/>），
/// 所以它跑通就等于界面跑得通。
///
/// <para>它要证明的是三层职责确实分开了：</para>
/// <list type="number">
/// <item>host 进程开一个 EasyMultiHost 跑 Core，再开一个<b>普通 EasyMultiClient</b> 加进去，走的是和远端玩家同一条路。</item>
/// <item>玩家只会 <c>Send</c> / <c>Received</c>，不碰任何房间管理。</item>
/// <item>谁发的言都经房主定序再广播回来（<b>包括发言者自己</b>），两端序号一致。</item>
/// </list>
///
///     godot --headless --path samples/ChatGodot res://Autopilot.tscn -- --role=host --token=xxx
///     godot --headless --path samples/ChatGodot res://Autopilot.tscn -- --role=guest --code=ABC123 --token=xxx
///
/// <para>地址和 token 从命令行来（默认 127.0.0.1:7777），和界面那边一样不写死。</para>
/// </summary>
public partial class Autopilot : Node
{
    private const string HostPlayerId = "HostPlayer";
    private const string GuestName = "Guest";

    private readonly List<string> _heard = new List<string>();

    private string _role = "host";
    private string _code = "";
    private string _relayHost = "127.0.0.1";
    private int _relayPort = 7777;
    private string _token = "demo-token";
    private double _timeout = 40.0;
    private double _quitIn = -1.0;
    private int _exitCode = 1;

    private EasyMultiHost _host;
    private EasyMultiClient _me;
    private ChatCore _core;
    private bool _spoke;

    public override void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--role=")) _role = arg.Substring("--role=".Length);
            else if (arg.StartsWith("--code=")) _code = arg.Substring("--code=".Length);
            else if (arg.StartsWith("--relay-host=")) _relayHost = arg.Substring("--relay-host=".Length);
            else if (arg.StartsWith("--relay-port=")) _relayPort = arg.Substring("--relay-port=".Length).ToInt();
            else if (arg.StartsWith("--token=")) _token = arg.Substring("--token=".Length);
        }

        GD.Print($"AUTOPILOT role={_role}");
        Net.Configure(_relayHost, _relayPort, _token); // 界面那边由表单调，这里由命令行调
        if (_role == "host") StartHost();
        else JoinAsPlayer(GuestName, _code);
    }

    /// <summary>房主进程：先起 Host（Core 在它上面跑），再用一个普通 EasyMultiClient 加进去。</summary>
    private void StartHost()
    {
        _host = EasyMulti.Host.Open(HostPlayerId, "AutoRoom", players: 8);
        _host.Opened += code =>
        {
            _core = new ChatCore(_host);
            GD.Print("CORE-UP");
            GD.Print("ROOM=" + code);            // 测试脚本从这里读房码
            JoinAsPlayer(HostPlayerId, code);  // ← 和远端玩家调用的是同一个方法
        };
        _host.PlayersChanged += players => GD.Print("HOST-SEES players=" + string.Join(",", players));
        _host.Rejected += r => GD.Print("HOST-TROUBLE " + r);
        _host.Disconnected += r => GD.Print("HOST-TROUBLE " + r);
    }

    /// <summary>玩家。本地房主还是远端房主，这段代码完全一样。</summary>
    private void JoinAsPlayer(string name, string code)
    {
        _me = EasyMulti.Client.Connect(name);
        _me.Joined += c =>
        {
            GD.Print($"JOINED={c} as {name}");
            if (_role == "guest") SayOnce("hello-from-guest");
        };
        _me.Receive<SayMsg>(OnSay);
        _me.Receive<WhoMsg>(OnWho);
        _me.Rejected += r => GD.Print("PLAYER-TROUBLE " + r);
        _me.Disconnected += r => GD.Print("PLAYER-TROUBLE " + r);
        _me.Join(code); // 不用等连上
    }

    private void OnWho(WhoMsg m)
    {
        string players = string.Join(",", m.Players);
        GD.Print($"WHO seq={m.Seq} players={players}");
        // 房主那个本地玩家等看到 guest 进来了再开口，保证 guest 一定收得到。
        if (_role == "host" && players.Contains(GuestName)) SayOnce("hi-from-host-player");
    }

    private void OnSay(SayMsg m)
    {
        GD.Print($"SAY seq={m.Seq} from={m.Who} text={m.Text}");
        _heard.Add($"{m.Seq}:{m.Who}:{m.Text}");
        CheckDone();
    }

    private void SayOnce(string text)
    {
        if (_spoke) return;
        _spoke = true;
        _me.Send(text); // 玩家只管发，路由不是它的事
        GD.Print($"SENT {text}");
    }

    /// <summary>两边都必须收到「自己的」和「对方的」发言，且都带 Core 发的序号。</summary>
    private void CheckDone()
    {
        bool sawGuest = _heard.Exists(h => h.Contains($":{GuestName}:hello-from-guest"));
        bool sawHostPlayer = _heard.Exists(h => h.Contains($":{HostPlayerId}:hi-from-host-player"));
        if (!sawGuest || !sawHostPlayer) return;

        GD.Print("HEARD-ALL " + string.Join(" ; ", _heard));
        GD.Print("PASS");
        _exitCode = 0;
        _quitIn = 1.5;
    }

    public override void _Process(double delta)
    {
        if (_quitIn > 0)
        {
            _quitIn -= delta;
            if (_quitIn <= 0) GetTree().Quit(_exitCode);
            return;
        }

        _timeout -= delta;
        if (_timeout <= 0)
        {
            GD.Print("TIMEOUT heard=" + string.Join(" ; ", _heard));
            GetTree().Quit(1);
        }
    }
}
