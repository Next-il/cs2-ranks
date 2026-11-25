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

[MinimumApiVersion(260)]
public class RanksFakeRank : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[Ranks] Fake Rank";
    public override string ModuleVersion => "v1.0.5";

    private Config _config;
    private IRanksApi? _api;
    private int _maxRank;

    /// <summary>IN_SCORE (1 << 33): the old check was Buttons.ToString() containing its digits, 8589934592.</summary>
    private const ulong ScoreboardButton = 1UL << 33;

    private const int Wins = 111;
    private const int MaxSlots = 65;

    /// <summary>The reveal to every player, about once a second at 64 tick.</summary>
    private const int RevealEveryTicks = 64;

    /// <summary>This tick's players and the badge each must show, filled in OnTick, re-pinned after entity think.</summary>
    private readonly List<(CCSPlayerController Player, sbyte Type, int Ranking)> _targets = new(MaxSlots);

    /// <summary>Whether each slot held TAB last tick: the reveal goes out on the press, not every tick it is held.</summary>
    private readonly bool[] _tabDown = new bool[MaxSlots];
    private readonly bool[] _seen = new bool[MaxSlots];

    // How often the game's own values had to be overwritten, logged once a minute: where the game resets
    // the badge (before or during entity think) is what decides whether a write reaches the snapshot.
    private int _fixedInTick, _fixedAfterThink;
    private DateTime _nextReport = DateTime.UtcNow.AddMinutes(1);

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IRanksApi.Capability.Get();
        if (_api == null) return;

        _config = _api.LoadConfig<Config>("ranks_fakerank");
        _maxRank = _config.FakeRank.Max(f => f.Key);

        RegisterListener<Listeners.OnTick>(OnTick);

        // v1.0.4: OnTick runs before the entities think, and the game resets these fields as often as every
        // tick. A reset that lands after our OnTick write goes out in that tick's snapshot, so clients saw the
        // game's value and ours in turn - the whole scoreboard column flickering between ranks and the "no
        // rank" icon (2026-11-25). Pinning again after entity think makes ours the last write before the
        // snapshot. The unchanged case is three reads.
        RegisterListener<Listeners.OnServerPostEntityThink>(() =>
        {
            foreach (var (player, type, ranking) in _targets)
            {
                if (player.IsValid && Pin(player, type, ranking))
                    _fixedAfterThink++;
            }
        });

        AddCommand("css_fakerank_reload", "", (player, info) =>
        {
            if (player != null) return;

            _config = _api.LoadConfig<Config>("ranks_fakerank");
            _maxRank = _config.FakeRank.Max(f => f.Key);
        });
    }

    private void OnTick()
    {
        if (_api == null) return;

        _targets.Clear();
        Array.Clear(_seen);
        var filter = new RecipientFilter();
        var revealAll = Server.TickCount % RevealEveryTicks == 0;

        foreach (var player in Utilities.GetPlayers()
                     .Where(u => u is { IsValid: true, Connected: PlayerConnectedState.Connected, IsBot: false, IsHLTV: false }))
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

            // The reveal goes to everyone once a second, and to a player the tick their TAB goes down. With only
            // the TAB press (v1.0.4) the badges blanked for about two frames on every open - the client hides
            // other players' ranks until the reveal reaches it, one round trip after the press (owner's clip,
            // Retakes #6, 2026-11-25). Sent ahead, the scoreboard can open already revealed. A reveal only
            // redraws badges whose values changed, and since v1.0.4 they no longer change, so the extra ones are
            // harmless - v1.0.3's flicker came from the game's resets reaching clients, which every reveal
            // (64 a second while TAB was held) then repainted.
            var slot = player.Slot;
            if (slot is < 0 or >= MaxSlots) continue;

            _seen[slot] = true;
            var tab = ((ulong)player.Buttons & ScoreboardButton) != 0;
            if (revealAll || (tab && !_tabDown[slot]))
                filter.Add(player);
            _tabDown[slot] = tab;
        }

        // A slot nobody holds now starts released, so the next player in it gets a reveal on their first TAB
        for (var i = 0; i < MaxSlots; i++)
        {
            if (!_seen[i]) _tabDown[i] = false;
        }

        if (filter.Count > 0)
        {
            var msg = UserMessage.FromPartialName("CCSUsrMsg_ServerRankRevealAll");
            msg.Send(filter);
        }

        if (DateTime.UtcNow >= _nextReport)
        {
            if (_fixedInTick + _fixedAfterThink > 0)
                Logger.LogInformation("[FakeRank] game resets overwritten in the last minute: {InTick} in OnTick, {AfterThink} after entity think",
                    _fixedInTick, _fixedAfterThink);

            _fixedInTick = _fixedAfterThink = 0;
            _nextReport = DateTime.UtcNow.AddMinutes(1);
        }
    }

    /// <summary>
    /// Writes the badge when the controller holds anything else. Compared with what it holds NOW, not with what
    /// was last written: the game resets these fields on its own, so a write-once cache left the badge blank
    /// (2026-11-24, v6). Returns whether anything had to be written.
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