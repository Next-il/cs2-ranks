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
    public override string ModuleVersion => "v1.0.6";

    private Config _config;
    private IRanksApi? _api;
    private int _maxRank;

    /// <summary>IN_SCORE (1 << 33): upstream checked Buttons.ToString() for its digits, 8589934592.</summary>
    private const ulong ScoreboardButton = 1UL << 33;

    private const int Wins = 111;
    private const int MaxSlots = 65;

    /// <summary>Whether each slot held TAB last tick: the reveal goes out on the press, like the Metamod FakeRanks Reveal All.</summary>
    private readonly bool[] _tabDown = new bool[MaxSlots];
    private readonly bool[] _seen = new bool[MaxSlots];

    // How often the controller held anything but our badge when OnTick came round, logged once a minute.
    // v1.0.4 counted ~5000 a minute on a 10-player server while it called SetStateChanged; this shows
    // whether the game still fights the badge without it.
    private int _drift;
    private DateTime _nextReport = DateTime.UtcNow.AddMinutes(1);

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IRanksApi.Capability.Get();
        if (_api == null) return;

        _config = _api.LoadConfig<Config>("ranks_fakerank");
        _maxRank = _config.FakeRank.Max(f => f.Key);

        RegisterListener<Listeners.OnTick>(OnTick);

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

        Array.Clear(_seen);
        var filter = new RecipientFilter();

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

            // v1.0.6 = upstream's write again: the fields every tick, and NO SetStateChanged. Upstream ran
            // like this for months with steady badges. v1.0.2-v1.0.5 (2026-11-24/25) flagged the fields
            // changed, and from then on the game reset them ~90 times a second and the badges flickered;
            // the 11-24 "only the tag" report came while the Metamod reveal was switched off after the
            // crash, not from this write.
            if (player.CompetitiveWins != Wins || player.CompetitiveRankType != rankType || player.CompetitiveRanking != rankValue)
            {
                _drift++;
                player.CompetitiveWins = Wins;
                player.CompetitiveRankType = rankType;
                player.CompetitiveRanking = rankValue;
            }

            var slot = player.Slot;
            if (slot is < 0 or >= MaxSlots) continue;

            _seen[slot] = true;
            var tab = ((ulong)player.Buttons & ScoreboardButton) != 0;
            if (tab && !_tabDown[slot])
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
            Logger.LogInformation("[FakeRank] badges found changed by the game in the last minute: {Drift}", _drift);
            _drift = 0;
            _nextReport = DateTime.UtcNow.AddMinutes(1);
        }
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
