using System.Collections.Generic;
using System.Linq;
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
    public override string ModuleVersion => "v1.0.3";

    private Config _config;
    private IRanksApi? _api;
    private int _maxRank;

    /// <summary>IN_SCORE (1 << 33): the old check was Buttons.ToString() containing its digits, 8589934592.</summary>
    private const ulong ScoreboardButton = 1UL << 33;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IRanksApi.Capability.Get();
        if (_api == null) return;

        _config = _api.LoadConfig<Config>("ranks_fakerank");
        _maxRank = _config.FakeRank.Max(f => f.Key);
        RegisterListener<Listeners.OnTick>(() =>
        {
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

                // Compared with what the controller holds NOW, not with what was last written: the game
                // resets these fields on its own (round start, team change), so a write-once cache left the
                // badge blank after the first reset (2026-11-24, v6). Any drift is rewritten and marked
                // changed, so it is networked; an unchanged tick writes nothing.
                if (player.CompetitiveRankType != rankType || player.CompetitiveRanking != rankValue || player.CompetitiveWins != 111)
                {
                    player.CompetitiveWins = 111;
                    player.CompetitiveRankType = rankType;
                    player.CompetitiveRanking = rankValue;
                    Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompetitiveWins");
                    Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompetitiveRankType");
                    Utilities.SetStateChanged(player, "CCSPlayerController", "m_iCompetitiveRanking");
                }

                // Holding TAB: the scoreboard shows every rank, not only the viewer's own
                if (((ulong)player.Buttons & ScoreboardButton) != 0)
                    filter.Add(player);
            }

            if (filter.Count > 0)
            {
                var msg = UserMessage.FromPartialName("CCSUsrMsg_ServerRankRevealAll");
                msg.Send(filter);
            }
        });

        AddCommand("css_fakerank_reload", "", (player, info) =>
        {
            if (player != null) return;

            _config = _api.LoadConfig<Config>("ranks_fakerank");
            _maxRank = _config.FakeRank.Max(f => f.Key);
        });
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