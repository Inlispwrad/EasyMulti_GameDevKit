using System.Collections.Generic;
using EasyMultiNet;

/// <summary>
/// 权威逻辑（Core）—— 这个聊天室里唯一有资格决定「谁说的话、按什么顺序、送给谁」的地方。
///
/// <para>
/// 它只在房主那边跑，而且<b>不知道中继、UDP、房码、Godot 的存在</b>：
/// 进来的是「某个玩家说了什么」，出去的是「第 N 条消息」。
/// 想改成独立服务器进程，把它连同那个 <see cref="EasyMultiHost"/> 搬过去就行，一行都不用改。
/// </para>
/// <para>
/// 它对「本地玩家」和「远端玩家」<b>没有任何区别对待</b> —— 房主自己那个玩家也是
/// 用普通 <see cref="EasyMultiClient"/> 接进来的。逻辑因此不会打结。
/// </para>
/// </summary>
public sealed class ChatCore
{
    private const int MaxTextLength = 500;

    /// <summary>新人进来时补发多少条历史。这是 Core 说了算的东西，玩家改不了。</summary>
    private const int BacklogSize = 30;

    private readonly EasyMultiHost _host;
    private readonly List<SayMsg> _backlog = new List<SayMsg>();
    private readonly HashSet<string> _seen = new HashSet<string>();
    private int _seq;

    public ChatCore(EasyMultiHost host)
    {
        _host = host;
        _host.Receive<string>(OnPlayerSaid); // 玩家只会发一句话（T=string 一条通道）
        _host.PlayersChanged += OnPlayersChanged;
    }

    /// <summary>某个玩家发来一句话：校验、定序，然后广播给<b>所有</b>玩家（包括他自己）。</summary>
    private void OnPlayerSaid(string from, string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > MaxTextLength) return; // 只有权威侧的校验算数

        _seq++;
        var say = new SayMsg(_seq, from, text);

        _backlog.Add(say);
        if (_backlog.Count > BacklogSize) _backlog.RemoveAt(0);

        _host.Broadcast(say);
    }

    /// <summary>
    /// 人员变动：把当前名单广播给所有人；对<b>刚进来</b>的那些人，再单独补一份最近的对话。
    /// 补发用定向发，不打扰已经在场的人 —— 这就是「新人入场要同步局面」的通用做法。
    /// </summary>
    private void OnPlayersChanged(IReadOnlyList<string> players)
    {
        _seq++;
        _host.Broadcast(new WhoMsg(_seq, System.Linq.Enumerable.ToArray(players)));

        var current = new HashSet<string>(players);
        _seen.RemoveWhere(name => !current.Contains(name)); // 走了的人，下次再来算新人

        foreach (string name in players)
        {
            if (!_seen.Add(name)) continue; // 老面孔，不用补
            foreach (SayMsg say in _backlog) _host.Send(name, say);
        }
    }
}
