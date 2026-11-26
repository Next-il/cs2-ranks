using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using RanksApi;

namespace Ranks_FakeRank;

/// <summary>
/// The scoreboard badge from the Ranks level.
///
/// <para><b>Write.</b> The game resets the three competitive fields constantly while players are on (up to
/// ~6000 times a minute on a 10-player server, with or without SetStateChanged - v1.0.6's log), most of it
/// during entity think, which runs after OnTick. So the badge is checked in OnTick and again after entity
/// think, and written plus marked changed only when the controller holds anything else. That keeps ours as
/// the last value before every snapshot (v1.0.4: 0-9 drifts a minute were left for OnTick to fix).</para>
///
/// <para><b>Reveal.</b> No reveal is sent on TAB any more: every CCSUsrMsg_ServerRankRevealAll makes the
/// client repaint the badges, so a reveal on each TAB press was the 1-2 frame flicker on every scoreboard
/// open (2026-11-25/26). A player is revealed once, about a second after they connect, and everyone again
/// on round_prestart. The Metamod FakeRanks Reveal All sends one on every TAB press too, so it has to be
/// off on servers that run this module.</para>
/// </summary>
[MinimumApiVersion(260)]
public class RanksFakeRank : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[Ranks] Fake Rank";
    public override string ModuleVersion => "v1.0.7";

    private Config _config;
    private IRanksApi? _api;
    private int _maxRank;

    private const int Wins = 111;
    private const int MaxSlots = 65;
    private static readonly TimeSpan RevealDelay = TimeSpan.FromSeconds(1);

    /// <summary>This tick's players and the badge each must show, filled in OnTick, re-pinned after entity think.</summary>
    private readonly List<(CCSPlayerController Player, sbyte Type, int Ranking)> _targets = new(MaxSlots);

    /// <summary>When each slot's connect reveal is due; null = nothing pending (sent, or nobody there).</summary>
    private readonly DateTime?[] _revealDue = new DateTime?[MaxSlots];

    // How often the game's values had to be overwritten, logged once a minute.
    private int _fixedInTick, _fixedAfterThink;
    private DateTime _nextReport = DateTime.UtcNow.AddMinutes(1);

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IRanksApi.Capability.Get();
        if (_api == null) return;

        _config = _api.LoadConfig<Config>("ranks_fakerank");
        _maxRank = _config.FakeRank.Max(f => f.Key);

        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnServerPostEntityThink>(() =>
        {
            foreach (var (player, type, ranking) in _targets)
            {
                if (player.IsValid && Pin(player, type, ranking))
                    _fixedAfterThink++;
            }
        });

        RegisterEventHandler<EventPlayerConnectFull>((@event, _) =>
        {
            var player = @event.Userid;
            if (player is { IsValid: true, IsBot: false, IsHLTV: false } && player.Slot is >= 0 and < MaxSlots)
                _revealDue[player.Slot] = DateTime.UtcNow + RevealDelay;
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDisconnect>((@event, _) =>
        {
            var player = @event.Userid;
            if (player != null && player.Slot is >= 0 and < MaxSlots)
                _revealDue[player.Slot] = null;
            return HookResult.Continue;
        });

        RegisterEventHandler<EventRoundPrestart>((_, _) =>
        {
            RevealTo(HumanPlayers().ToList());
            return HookResult.Continue;
        });

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            Array.Clear(_revealDue);
            _targets.Clear();
        });

        // Loaded on a running server (restart, hot reload): everyone already on gets their connect reveal
        foreach (var player in HumanPlayers())
        {
            if (player.Slot is >= 0 and < MaxSlots)
                _revealDue[player.Slot] = DateTime.UtcNow + RevealDelay;
        }

        AddCommand("css_fakerank_reload", "", (player, info) =>
        {
            if (player != null) return;

            _config = _api.LoadConfig<Config>("ranks_fakerank");
            _maxRank = _config.FakeRank.Max(f => f.Key);
        });
    }

    private static IEnumerable<CCSPlayerController> HumanPlayers() =>
        Utilities.GetPlayers().Where(u => u is { IsValid: true, Connected: PlayerConnectedState.Connected, IsBot: false, IsHLTV: false });

    private void OnTick()
    {
        if (_api == null) return;

        _targets.Clear();
        var now = DateTime.UtcNow;
        List<CCSPlayerController>? due = null;

        foreach (var player in HumanPlayers())
        {
            sbyte rankType;
            int rankValue;

            if (_config.Type is 0)
            {
                rankType = 11;
                rankValue = _api.GetPlayerExperience(player);
            }
            else
            {
                rankType = 12;
                var rank = _api.GetPlayerRank(player);

                rankValue = _config.FakeRank[rank > _maxRank ? _maxRank : rank <= 0 ? 1 : rank];
            }

            _targets.Add((player, rankType, rankValue));
            if (Pin(player, rankType, rankValue))
                _fixedInTick++;

            var slot = player.Slot;
            if (slot is >= 0 and < MaxSlots && _revealDue[slot] is { } at && now >= at)
            {
                _revealDue[slot] = null;
                (due ??= new List<CCSPlayerController>()).Add(player);
            }
        }

        if (due != null)
            RevealTo(due);

        if (now >= _nextReport)
        {
            if (_fixedInTick + _fixedAfterThink > 0)
                Logger.LogInformation("[FakeRank] game resets overwritten in the last minute: {InTick} in OnTick, {AfterThink} after entity think",
                    _fixedInTick, _fixedAfterThink);

            _fixedInTick = _fixedAfterThink = 0;
            _nextReport = now.AddMinutes(1);
        }
    }

    private static void RevealTo(List<CCSPlayerController> players)
    {
        if (players.Count == 0) return;

        var filter = new RecipientFilter();
        foreach (var player in players)
            filter.Add(player);

        var msg = UserMessage.FromPartialName("CCSUsrMsg_ServerRankRevealAll");
        msg.Send(filter);
    }

    /// <summary>
    /// Writes the badge when the controller holds anything else, and only then marks the fields changed.
    /// Compared with what it holds NOW, not with what was last written: the game resets these fields on its
    /// own, so a write-once cache left the badge blank (2026-11-24, v1.0.2). Returns whether it wrote.
    /// </summary>
    private static bool Pin(CCSPlayerController player, sbyte rankType, int rankValue)
    {
        if (player.CompetitiveRankType == rankType && player.CompetitiveRanking == rankValue && player.CompetitiveWins == Wins)
            return false;

        player.CompetitiveWins = Wins;
        player.CompetitiveRankType = rankType;
        player.CompetitiveRanking = rankValue;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompetitiveWins");
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompetitiveRankType");
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompetitiveRanking");
        return true;
    }
}

public class Config
{
    public int Type { get; set; } = 1;

    public Dictionary<int, int> FakeRank { get; set; } = new()
    {
        [1] = 1,
        [2] = 2,
        [3] = 3,
        [4] = 4,
        [5] = 5,
        [6] = 6,
        [7] = 7,
        [8] = 8,
        [9] = 9,
        [10] = 10,
        [11] = 11,
        [12] = 12,
        [13] = 13,
        [14] = 14,
        [15] = 15,
        [16] = 16,
        [17] = 17,
        [18] = 18,
    };
}
