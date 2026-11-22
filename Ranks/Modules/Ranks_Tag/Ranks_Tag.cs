using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using RanksApi;

namespace Ranks_Tag;

public class RanksTag : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[Ranks] Tag";
    public override string ModuleVersion => "v1.0.1-next";

    private IRanksApi? _api;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IRanksApi.Capability.Get();
        if (_api == null) return;

        AddTimer(5f, () =>
        {
            foreach (var player in Utilities.GetPlayers().Where(u => u.IsValid))
            {
                var level = _api.GetLevelFromExperience(_api.GetPlayerExperience(player));

                // Next-il: the bare rank name. The scoreboard now draws its own [ ] around a clan tag, so the
                // old "[Gold Nova]" showed as "[[Gold Nova]]" (owner, 2026-11-22). Only sent when it changed.
                var tag = Regex.Replace(level.Name, @"\{[A-Za-z]+}", "").Trim();
                if (player.Clan == tag) continue;

                player.Clan = tag;
                Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
            }
        }, TimerFlags.REPEAT);
    }
}