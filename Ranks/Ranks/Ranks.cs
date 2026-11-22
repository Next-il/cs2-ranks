using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using PanoramaManager;
using Ranks.Hud;
using RanksApi;

namespace Ranks;

// Next-il fork of thesamefabius' [Ranks] Core v2.0.5 (Next-il/cs2-ranks). What changed:
//  - XP is granted per event: every part of one kill is summed, the bonus is applied once, rounded once (Xp.cs).
//  - The XP bonuses live here now (Bonuses.cs): VIP from VIP Core, the name reward, the weekend bonus. They add up and
//    are capped by Bonuses.MaxMultiplier. This replaces the VIP_ExperienceMultiplier module of our VIP Core fork.
//  - Three Panorama HUDs replace the chat lines and the CenterHtml menu (Hud/): the rank panel, the XP feed with the
//    RANK UP banner, and the /rank menu, which /top opens on its Top players tab. Hud.Enabled = false brings back one
//    batched chat line per event and the old menus.
//  - Rank changes fire at the top rank too; the stored rank is synced quietly on join instead of announced.

[MinimumApiVersion(210)]
public class Ranks : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius, Next-il";
    public override string ModuleDescription => "Adds a rating system to the server";
    public override string ModuleName => "[Ranks] Core";
    public override string ModuleVersion => "v2.0.5-next";

    public string DbConnectionString = string.Empty;

    public Config Config = null!;
    public Database Database = null!;
    public RanksApi RanksApi = null!;
    public BonusService Bonuses = null!;
    public HudPrefsStore Prefs = null!;

    public readonly ConcurrentDictionary<ulong, User> Users = new();
    private readonly DateTime[] _loginTime = new DateTime[64];

    public readonly FakeConVar<bool> RanksEnable = new("css_lr_enable", "", true);

    // Next-il: the name reward. Same names as the VIP_ExperienceMultiplier module used, so the lines already in
    // multicfg/general.cfg keep working. Matched anywhere in the nickname, ignoring case.
    public readonly FakeConVar<string> NameRewardName =
        new("ranks_namereward_name", "Nickname text that earns the XP name reward", "Next");

    public readonly FakeConVar<float> NameRewardMultiplier =
        new("ranks_namereward_experience_multiplier", "XP multiplier for the name reward", 1.5f);

    private RankPanel? _panel;
    private XpFeed? _feed;
    private RankMenu? _menu;

    /// <summary>Config.Ranks ordered by threshold, ties kept in file order. Rebuilt with the config.</summary>
    private List<(string Name, int Value)> _ladder = [];

    private CCSGameRulesProxy? _rules;

    private bool Hud => Config.Hud.Enabled && _feed != null;

    public override void Load(bool hotReload)
    {
        RanksApi = new RanksApi(this);
        Capabilities.RegisterPluginCapability(IRanksApi.Capability, () => RanksApi);

        Config = LoadConfig();
        BuildLadder();
        DbConnectionString = BuildConnectionString();
        Database = new Database(this, DbConnectionString);
        Task.Run(Database.CreateTable).ContinueWith((task) => {
            if (task.IsFaulted)
            {
                throw task.Exception;
            }
        });

        Bonuses = new BonusService(this);
        Prefs = new HudPrefsStore(this);

        if (Config.Hud.Enabled)
        {
            Panorama.Init(this);
            _panel = new RankPanel(this);
            _feed = new XpFeed(this);
            _menu = new RankMenu(this);
        }

        AddCommandListener("say", CommandListener_Say);
        AddCommandListener("say_team", CommandListener_Say);

        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _rules = null;
            _feed?.Reset();
        });

        RegisterEventHandler<EventWeaponFire>(EventWeaponFire);
        RegisterEventHandler<EventPlayerHurt>(EventPlayerHurt);
        RegisterEventHandler<EventPlayerDeath>(EventPlayerDeath);
        RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnect);
        RegisterEventHandler<EventPlayerConnectFull>(EventPlayerConnectFull);
        RegisterEventHandler<EventPlayerSpawn>(EventPlayerSpawn);
        RegisterEventHandler<EventPlayerTeam>(EventPlayerTeam);

        RegisterEventHandler<EventRoundStart>(EventRoundStart);
        RegisterEventHandler<EventRoundEnd>(EventRoundEnd);
        RegisterEventHandler<EventRoundMvp>(EventRoundMvp);

        BombEvents();
        CreateMenu();

        AddTimer(5.0f, UpdateUserStatsTimer, TimerFlags.REPEAT);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        VipBridge.Resolve(Logger);
    }

    public override void Unload(bool hotReload)
    {
        _menu?.Dispose();
        _feed?.Dispose();
        _panel?.Dispose();
    }

    // ------------------------------------------------------------------ commands

    [ConsoleCommand("css_rank")]
    public void OnCmdRank(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller == null) return;

        if (Hud)
        {
            _menu!.Open(controller, RankMenu.TabOverview);
            return;
        }

        var steamId = new SteamID(controller.SteamID);
        Task.Run(() => GetUserStats(controller, steamId));
    }

    [ConsoleCommand("css_xp")]
    public void OnCmdXp(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller == null || !Hud) return;

        var prefs = Prefs.Update(controller.SteamID, p => p.Feed = !p.Feed);
        _feed!.Push(controller, prefs.Feed
            ? new FeedLine("", Tag: Text("feed.on", "XP feed on"), Kind: FeedKind.Note, Always: true)
            : new FeedLine("", Tag: Text("feed.off", "XP feed off"), Tag2: Text("feed.off_hint", "/xp turns it on"),
                Kind: FeedKind.Note, Always: true));
        HudPrefsChanged(controller, prefs);
    }

    [RequiresPermissions("@css/root")]
    [CommandHelper(1, "<username or #userid or @steamid> [exp (def. 0)]")]
    [ConsoleCommand("css_lr_giveexp")]
    public void OnCmdGiveExp(CCSPlayerController? player, CommandInfo info)
    {
        var target = Utils.GetPlayer(info.GetArg(1));

        if (target == null)
        {
            ReplyToCommand(player, "Target is not valid!");
            return;
        }

        var exp = 0;
        if (info.ArgCount >= 3)
            exp = int.TryParse(info.GetArg(2), out var value) ? value : 0;

        RanksApi.GivePlayerExperience(target, exp);
        ReplyToCommand(player, Localizer["command.give_exp", exp, target.PlayerName]);
        ReplyToCommand(target, Localizer["command.target.give_exp", player?.PlayerName ?? "Console", exp]);
    }

    [RequiresPermissions("@css/root")]
    [CommandHelper(1, "<username or #userid or @steamid> [exp (def. 0)]")]
    [ConsoleCommand("css_lr_takeexp")]
    public void OnCmdTakeExp(CCSPlayerController? player, CommandInfo info)
    {
        var target = Utils.GetPlayer(info.GetArg(1));

        if (target == null)
        {
            ReplyToCommand(player, "Target is not valid!");
            return;
        }

        var exp = 0;
        if (info.ArgCount >= 3)
            exp = int.TryParse(info.GetArg(2), out var value) ? value : 0;

        RanksApi.TakePlayerExperience(target, exp);
        ReplyToCommand(player, Localizer["command.take_exp", exp, target.PlayerName]);
        ReplyToCommand(target, Localizer["command.target.take_exp", player?.PlayerName ?? "Console", exp]);
    }

    [ConsoleCommand("css_lr_reload")]
    public void OnCmdReloadCfg(CCSPlayerController? controller, CommandInfo info)
    {
        if (controller != null) return;

        Config = LoadConfig();
        BuildLadder();
        Logger.LogInformation("Configuration successfully rebooted (Hud.Enabled changes need a map change)");
    }

    [ConsoleCommand("css_top")]
    public void OnCmdTop(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller == null) return;

        if (Hud)
        {
            _menu!.Open(controller, RankMenu.TabTop);
            return;
        }

        Task.Run(() => ShowTopPlayers(controller));
    }

    private async Task ShowTopPlayers(CCSPlayerController controller)
    {
        var topPlayersSorted = await Database.GetTopPlayers();

        if (topPlayersSorted == null) return;

        await Server.NextFrameAsync(() =>
        {
            controller.PrintToChat(Localizer["top.Title"]);
            var rank = 1;
            foreach (var player in topPlayersSorted)
            {
                if (!controller.IsValid) continue;

                controller.PrintToChat(
                    $"{rank++}. {Localizer["top.Players", player.name, ReplaceColorTags(GetLevelFromExperience(player.value).Name), player.value]}");
            }
        });
    }

    private HookResult CommandListener_Say(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return HookResult.Continue;
        var msg = Utils.GetTextInsideQuotes(info.ArgString);

        if (msg.StartsWith('!') || msg.StartsWith('/')) return HookResult.Continue;

        if (Config.UseCommandWithoutPrefix)
        {
            switch (msg)
            {
                case "rank":
                    OnCmdRank(player, info);
                    return HookResult.Continue;
                case "top":
                    OnCmdTop(player, info);
                    return HookResult.Continue;
                case "xp":
                    OnCmdXp(player, info);
                    return HookResult.Continue;
            }
        }

        return HookResult.Continue;
    }

    // ------------------------------------------------------------------ players

    private void OnClientAuthorized(int slot, SteamID id)
    {
        var player = Utilities.GetPlayerFromSlot(slot);

        if (player is null || player.IsBot) return;
        var playerName = player.PlayerName;

        Task.Run(() => OnClientAuthorizedAsync(slot, playerName, id));
        _loginTime[player.Slot] = DateTime.UtcNow;
    }

    private async Task OnClientAuthorizedAsync(int slot, string playerName, SteamID steamId)
    {
        var userExists = await Database.UserExists(steamId.SteamId2);

        if (!userExists)
            await Database.AddUserToDb(playerName, steamId.SteamId2);

        var user = await Database.GetUserStatsFromDb(steamId.SteamId2);

        if (user == null) return;

        var initPoints = Config.InitialExperiencePoints;
        if (user.value <= 0 && initPoints > 0)
            user.value = initPoints;

        // The stored rank can be stale - a new player is 0, and thresholds change. Synced here, quietly: a rank change
        // is announced when XP moves it, not when someone joins.
        user.rank = GetLevelFromExperience(user.value).Level;

        Users[steamId.SteamId64] = new User
        {
            steam = user.steam,
            name = user.name,
            value = user.value,
            rank = user.rank,
            kills = user.kills,
            deaths = user.deaths,
            shoots = user.shoots,
            hits = user.hits,
            headshots = user.headshots,
            assists = user.assists,
            round_win = user.round_win,
            round_lose = user.round_lose,
            playtime = user.playtime,
            lastconnect = user.lastconnect
        };

        if (Config.Hud.Enabled) await Prefs.LoadAsync(steamId.SteamId64);

        await Server.NextFrameAsync(() =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true, Connected: PlayerConnectedState.Connected } && player.SteamID == steamId.SteamId64)
                AttachHud(player);
        });
    }

    private HookResult EventPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true, IsBot: false } player && Users.ContainsKey(player.SteamID))
            AttachHud(player);

        return HookResult.Continue;
    }

    private void AttachHud(CCSPlayerController player)
    {
        if (!Hud) return;
        _feed!.Attach(player);
        _panel!.Sync(player);
    }

    private HookResult EventPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        SyncPanelNextFrame(@event.Userid);
        return HookResult.Continue;
    }

    private HookResult EventPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        SyncPanelNextFrame(@event.Userid);
        return HookResult.Continue;
    }

    /// <summary>The pawn's life state settles after the event, so the panel is judged a frame later.</summary>
    private void SyncPanelNextFrame(CCSPlayerController? player)
    {
        if (!Hud || player is not { IsValid: true, IsBot: false }) return;

        var slot = player.Slot;
        var steamId = player.SteamID;
        Server.NextFrame(() =>
        {
            var current = Utilities.GetPlayerFromSlot(slot);
            if (current is { IsValid: true } && current.SteamID == steamId) _panel!.Sync(current);
        });
    }

    /// <summary>Called by the menu's Settings tab and by /xp after the player changed a choice.</summary>
    public void HudPrefsChanged(CCSPlayerController player, HudPrefs prefs)
    {
        if (!Hud) return;
        _panel!.Sync(player);
        _panel.Refresh(player);
    }

    private void UpdateUserStatsTimer()
    {
        foreach (var player in Utilities.GetPlayers().Where(u => u.IsValid))
        {
            if (!Users.TryGetValue(player.SteamID, out var user)) continue;

            var steamId = new SteamID(player.SteamID);
            var totalTime = GetTotalTime(player.Slot);

            Task.Run(() =>
                Database.UpdateUserStatsDb(steamId, user, totalTime, DateTimeOffset.Now.ToUnixTimeSeconds()));
        }
    }

    private HookResult EventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is null || !player.IsValid) return HookResult.Continue;

        _panel?.Forget(player.Slot);
        _feed?.Forget(player.Slot);
        _menu?.Forget(player.Slot);
        Prefs.Forget(player.SteamID);

        if (Users.TryGetValue(player.SteamID, out var user))
        {
            var steamId = new SteamID(player.SteamID);
            var totalTime = GetTotalTime(player.Slot);
            _loginTime[player.Slot] = DateTime.MinValue;

            user.name = player.PlayerName;
            Task.Run(() =>
                Database.UpdateUserStatsDb(steamId, user, totalTime, DateTimeOffset.Now.ToUnixTimeSeconds()));
            Users.Remove(player.SteamID, out var _);
        }

        return HookResult.Continue;
    }

    // ------------------------------------------------------------------ XP events

    private HookResult EventWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
            Grant(player, new XpGrant { Stats = { Hits = 1 } });

        return HookResult.Continue;
    }

    private HookResult EventPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;

        if (attacker is { IsValid: true, IsBot: false })
            Grant(attacker, new XpGrant { Stats = { Shoots = 1 } });

        return HookResult.Continue;
    }

    private HookResult EventPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        var assister = @event.Assister;

        if (victim is { IsValid: true, IsBot: false }) SyncPanelNextFrame(victim);

        if (Config.MinPlayers > PlayersCount)
            return HookResult.Continue;

        var configEvent = Config.Events.EventPlayerDeath;
        var additionally = Config.Events.Additionally;

        var expAttacker = 0;

        if (attacker is not null && attacker.IsValid)
        {
            if (attacker.IsBot || victim is not null && victim.IsBot)
                return HookResult.Continue;

            if (attacker != victim)
            {
                if (victim is not null && attacker.TeamNum == victim.TeamNum && !Config.TeamKillAllowed)
                {
                    Grant(attacker, new XpGrant().Add(XpPart.TeamKill, configEvent.KillingAnAlly));
                }
                else
                {
                    var weaponName = @event.Weapon;

                    if (Regex.Match(weaponName, "knife").Success || Regex.Match(weaponName, "bayonet").Success)
                        weaponName = "knife";

                    // One grant for the whole kill: the kill, then each extra the event carries.
                    var kill = new XpGrant { Stats = { Kills = 1 } };

                    if (Config.TypeStatistics is 1)
                    {
                        var victimXp = victim != null && Users.TryGetValue(victim.SteamID, out var v) ? v.value : 0;
                        var attackerXp = Users.TryGetValue(attacker.SteamID, out var a) && a.value != 0 ? a.value : 1;
                        expAttacker = (victimXp / attackerXp * 5.0f).RoundToNearest();

                        if (expAttacker < 1)
                            expAttacker = 1;

                        kill.Add(XpPart.Kill, expAttacker);
                    }
                    else
                        kill.Add(XpPart.Kill, configEvent.Kills);

                    if (@event.Penetrated > 0) kill.Add(XpPart.Wallbang, additionally.Penetrated);
                    if (@event.Thrusmoke) kill.Add(XpPart.Smoke, additionally.Thrusmoke);
                    if (@event.Noscope) kill.Add(XpPart.Noscope, additionally.Noscope);
                    if (@event.Headshot)
                    {
                        kill.Add(XpPart.Headshot, additionally.Headshot);
                        kill.Stats.Headshots = 1;
                    }
                    if (@event.Attackerblind) kill.Add(XpPart.Blind, additionally.Attackerblind);
                    if (Config.Weapon.TryGetValue(weaponName, out var exp)) kill.Add(XpPart.Weapon, exp);

                    Grant(attacker, kill, weaponName);
                }
            }
        }

        if (victim is not null && victim.IsValid)
        {
            if (victim.IsBot)
                return HookResult.Continue;

            if (attacker != victim)
            {
                if (Config.TypeStatistics is 1)
                {
                    var expVictim = (expAttacker * Config.KillCoefficient).RoundToNearest();
                    if (expVictim < 1)
                    {
                        expVictim = 1;
                    }

                    Grant(victim, new XpGrant { Stats = { Deaths = 1 } }.Add(XpPart.Death, -expVictim));
                }
                else
                    Grant(victim, new XpGrant { Stats = { Deaths = 1 } }.Add(XpPart.Death, configEvent.Deaths));
            }
            else
                Grant(victim, new XpGrant().Add(XpPart.Suicide, configEvent.Suicide));
        }

        if (assister is not null && assister.IsValid)
        {
            if (assister.IsBot) return HookResult.Continue;
            Grant(assister, new XpGrant { Stats = { Assists = 1 } }.Add(XpPart.Assist, configEvent.Assists));
        }

        return HookResult.Continue;
    }

    private HookResult EventRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        var playerCount = PlayersCount;
        if (Config.MinPlayers > playerCount && RanksEnable.Value)
        {
            PrintToChatAll(Localizer["NotEnoughPlayers", playerCount, Config.MinPlayers]);
        }

        return HookResult.Continue;
    }

    private HookResult EventRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (@event.Reason is (int)RoundEndReason.RoundDraw) return HookResult.Continue;
        var winner = @event.Winner;

        var configEvent = Config.Events.EventRoundEnd;

        if (Config.MinPlayers > PlayersCount) return HookResult.Continue;

        foreach (var player in Utilities.GetPlayers().Where(u => u is { IsValid: true, IsBot: false }))
        {
            if (player.Team is not CsTeam.Spectator)
            {
                if (player.TeamNum != winner)
                    Grant(player, new XpGrant { Stats = { RoundLose = 1 } }.Add(XpPart.RoundLoss, configEvent.Loser));
                else
                    Grant(player, new XpGrant { Stats = { RoundWin = 1 } }.Add(XpPart.RoundWin, configEvent.Winner));
            }
        }

        return HookResult.Continue;
    }

    private HookResult EventRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is null || !player.IsValid) return HookResult.Continue;

        Grant(player, new XpGrant().Add(XpPart.Mvp, Config.Events.EventRoundMvp));

        return HookResult.Continue;
    }

    private void BombEvents()
    {
        RegisterEventHandler<EventBombDropped>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player)
                Grant(player, new XpGrant().Add(XpPart.BombDrop, Config.Events.EventPlayerBomb.DroppedBomb));
            return HookResult.Continue;
        });

        RegisterEventHandler<EventBombDefused>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player)
                Grant(player, new XpGrant().Add(XpPart.BombDefuse, Config.Events.EventPlayerBomb.DefusedBomb));
            return HookResult.Continue;
        });

        RegisterEventHandler<EventBombPickup>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player)
                Grant(player, new XpGrant().Add(XpPart.BombPickup, Config.Events.EventPlayerBomb.PickUpBomb));
            return HookResult.Continue;
        });

        RegisterEventHandler<EventBombPlanted>((@event, _) =>
        {
            if (@event.Userid is { IsValid: true } player)
                Grant(player, new XpGrant().Add(XpPart.BombPlant, Config.Events.EventPlayerBomb.PlantedBomb));
            return HookResult.Continue;
        });
    }

    // ------------------------------------------------------------------ granting

    /// <summary>
    /// Applies one event's XP and stats. The parts are summed, the bonus multiplies the sum once (gains only - lost
    /// XP is never multiplied), and the result is rounded once, half away from zero. Modules still get one
    /// PlayerExperienceChanged per grant and may replace the amount.
    /// </summary>
    private void Grant(CCSPlayerController? player, XpGrant grant, string? weapon = null)
    {
        if (!RanksEnable.Value) return;
        if (player == null || Config.MinPlayers > PlayersCount || IsWarmup()) return;
        if (!Users.TryGetValue(player.SteamID, out var user)) return;

        var baseExp = grant.Base;
        BonusState? bonus = baseExp > 0 ? Bonuses.For(player) : null;
        var multiplier = bonus?.Total ?? 1f;
        var total = XpMath.Apply(baseExp, multiplier);

        if (baseExp != 0)
            total = RanksApi.OnPlayerExperienceChanged(player, total) ?? total;

        user.value += total;
        user.kills += grant.Stats.Kills;
        user.deaths += grant.Stats.Deaths;
        user.assists += grant.Stats.Assists;
        user.round_lose += grant.Stats.RoundLose;
        user.round_win += grant.Stats.RoundWin;
        user.headshots += grant.Stats.Headshots;
        user.hits += grant.Stats.Hits;
        user.shoots += grant.Stats.Shoots;

        if (user.value <= 0) user.value = Config.InitialExperiencePoints;

        if (total == 0) return;

        var result = new XpResult(grant, baseExp, total, multiplier, bonus?.Capped ?? false);
        var rankChange = CheckRank(player, user);

        if (Hud)
        {
            _feed!.Push(player, Line(result, weapon));
            if (rankChange is { } change) AnnounceRank(player, change.From, change.To);
            _panel!.Refresh(player);
            _menu!.Refresh(player);
        }
        else
        {
            ChatLine(player, user, result);
        }
    }

    /// <summary>Moves user.rank to where the XP now puts it and tells modules. Returns the levels when it moved.</summary>
    private (int From, int To)? CheckRank(CCSPlayerController player, User user)
    {
        var level = GetLevelFromExperience(user.value).Level;
        if (level == user.rank) return null;

        var from = user.rank;
        var promoted = level > from;
        user.rank = level;
        RanksApi.OnRankChanged(player, level, promoted);

        if (!Hud)
        {
            var name = ReplaceColorTags(GetLevelFromExperience(user.value).Name);
            PrintToChat(player, promoted ? Localizer["Up", name] : Localizer["Down", name]);
        }

        return (from, level);
    }

    private void AnnounceRank(CCSPlayerController player, int fromLevel, int toLevel)
    {
        var from = RankAt(fromLevel - 1, 0);
        var to = RankAt(toLevel - 1, 0);

        if (toLevel > fromLevel)
            _feed!.RankUp(player, from, to);
        else
            _feed!.Push(player, new FeedLine("", Tag: Text("feed.rank_down", "Rank down"), Tag2: to.Name, Kind: FeedKind.RankDown));

        _panel!.RankChanged(player);
    }

    private FeedLine Line(XpResult r, string? weapon)
    {
        var g = r.Grant;

        if (r.Total < 0)
        {
            var icons = g.Has(XpPart.Death) || g.Has(XpPart.Suicide) ? new[] { "sk" } : null;
            return new FeedLine(HudFormat.Signed(r.Total), Tag: Tag(g.Lead), Kind: FeedKind.Loss, Icons: icons);
        }

        var kill = g.Has(XpPart.Kill);
        var list = new List<string>();
        if (kill)
        {
            if (g.Has(XpPart.Headshot)) list.Add("hs");
            if (g.Has(XpPart.Wallbang)) list.Add("wb");
            if (g.Has(XpPart.Noscope)) list.Add("ns");
            if (g.Has(XpPart.Blind)) list.Add("bl");
            if (g.Has(XpPart.Smoke)) list.Add("sm");
            if (weapon == "knife") list.Add("kn");
        }

        return new FeedLine(
            HudFormat.Signed(r.Total),
            Original: r.Boosted ? HudFormat.Signed(r.Base) : "",
            Chip: r.Boosted ? HudFormat.Multiplier(r.Multiplier) : "",
            Max: r.Boosted && r.Capped,
            Tag: kill ? "" : Tag(g.Lead),
            Kind: r.Boosted ? FeedKind.Bonus : FeedKind.Plain,
            Icons: list);
    }

    private string Tag(XpPart part) => part switch
    {
        XpPart.Assist => Text("feed.assist", "Assist"),
        XpPart.Death => Text("feed.death", "Death"),
        XpPart.Suicide => Text("feed.suicide", "Suicide"),
        XpPart.TeamKill => Text("feed.teamkill", "Team kill"),
        XpPart.Mvp => Text("feed.mvp", "MVP"),
        XpPart.RoundWin => Text("feed.round_win", "Round won"),
        XpPart.RoundLoss => Text("feed.round_loss", "Round lost"),
        XpPart.BombDrop => Text("feed.bomb_drop", "Bomb dropped"),
        XpPart.BombDefuse => Text("feed.bomb_defuse", "Defused"),
        XpPart.BombPickup => Text("feed.bomb_pickup", "Bomb picked up"),
        XpPart.BombPlant => Text("feed.bomb_plant", "Planted"),
        _ => "",
    };

    /// <summary>Hud.Enabled = false: the old chat line, now one per event instead of one per part.</summary>
    private void ChatLine(CCSPlayerController player, User user, XpResult r)
    {
        if (!Config.ShowExperienceMessages) return;

        var label = r.Grant.Lead switch
        {
            XpPart.Kill => Localizer["PerKill"],
            XpPart.Assist => Localizer["AssistingInAKill"],
            XpPart.Death => Localizer["PerDeath"],
            XpPart.Suicide => Localizer["suicide"],
            XpPart.TeamKill => Localizer["KillingAnAlly"],
            XpPart.Mvp => Localizer["Mvp"],
            XpPart.RoundWin => Localizer["WinningRound"],
            XpPart.RoundLoss => Localizer["LosingRound"],
            XpPart.BombDrop => Localizer["dropping_bomb"],
            XpPart.BombDefuse => Localizer["defusing_bomb"],
            XpPart.BombPickup => Localizer["raising_bomb"],
            XpPart.BombPlant => Localizer["planting_bomb"],
            _ => Localizer["PerKill"],
        };
        var bonus = r.Boosted ? $" \x10{HudFormat.Multiplier(r.Multiplier)}" : "";
        var rank = RankFor(user.value);
        var next = rank.Next is { } n ? $"{Localizer["next_level", n.Value - user.value]}" : string.Empty;

        Server.NextFrame(() => PrintToChat(player,
            $"{(r.Total > 0 ? "\x0C+" : "\x02")}{r.Total} XP{bonus} \x08{label} {next}"));
    }

    // ------------------------------------------------------------------ ranks

    private void BuildLadder()
    {
        _ladder = Config.Ranks
            .Select((pair, order) => (Name: StripColors(pair.Key), pair.Value, Order: order))
            .OrderBy(r => r.Value)
            .ThenBy(r => r.Order)
            .Select(r => (r.Name, r.Value))
            .ToList();
    }

    private static string StripColors(string name) => Regex.Replace(name, @"\{[A-Za-z]+}", "").Trim();

    /// <summary>
    /// The HUD's view of the ladder. The index follows the level number the scoreboard uses (level N is the Nth entry
    /// by threshold), and the name is that entry's, so the name always matches the icon beside it.
    /// </summary>
    public RankInfo RankFor(long xp)
    {
        var level = GetLevelFromExperience(xp).Level;
        return RankAt(level - 1, xp);
    }

    private RankInfo RankAt(int index, long xp)
    {
        if (_ladder.Count == 0) return new RankInfo(0, "", 0, null, 1, _ladder);

        index = Math.Clamp(index, 0, _ladder.Count - 1);
        var current = _ladder[index];
        (string Name, int Value)? next = index + 1 < _ladder.Count ? _ladder[index + 1] : null;

        var progress = next is { } n && n.Value > current.Value
            ? Math.Clamp((xp - current.Value) / (double)(n.Value - current.Value), 0, 1)
            : 1;

        return new RankInfo(index, current.Name, current.Value, next, progress, _ladder);
    }

    public (string Name, int Level) GetLevelFromExperience(long experience)
    {
        foreach (var rank in Config.Ranks.OrderByDescending(pair => pair.Value))
        {
            if (experience >= rank.Value)
            {
                return (rank.Key, Config.Ranks.Count(pair => pair.Value <= rank.Value));
            }
        }

        return (string.Empty, 0);
    }

    // ------------------------------------------------------------------ old menus (Hud.Enabled = false)

    private void CreateMenu()
    {
        AddCommand("css_lvl", "", (player, _) =>
        {
            if (player == null) return;

            if (Hud)
            {
                _menu!.Open(player, RankMenu.TabOverview);
                return;
            }

            var title = Localizer["menu.title"];
            var menu = new CenterHtmlMenu(title, this);
            var ranksMenu = new CenterHtmlMenu(title, this);

            menu.AddMenuOption(Localizer["menu.allranks"], (_, _) => ranksMenu.Open(player));
            menu.AddMenuOption(Localizer["menu.reset"], (_, _) =>
            {
                var subMenu = new CenterHtmlMenu(Localizer["menu.reset.title"], this)
                {
                    PostSelectAction = PostSelectAction.Close
                };

                subMenu.AddMenuOption(Localizer["menu.reset.yes"], (_, _) =>
                {
                    var playerName = player.PlayerName;
                    var steamId = new SteamID(player.SteamID);
                    Task.Run(() => ResetRank(player.Slot, playerName, steamId));
                    PrintToChat(player, Localizer["menu.reset.complete"]);
                });

                subMenu.AddMenuOption(Localizer["menu.reset.no"],
                    (controller, option) => PrintToChat(controller, Localizer["reset.reset.canceled"]));

                subMenu.Open(player);
            }, !Config.StatisticsResetEnabled);

            foreach (var (rankName, rankValue) in Config.Ranks)
            {
                ranksMenu.AddMenuOption(Localizer["menu.allranks.show", rankName, rankValue], null!, true);
            }

            menu.AddMenuOption(Localizer["menu.top10"],
                (controller, _) => Task.Run(() => ShowTopPlayers(controller)));

            RanksApi.OnCreatedMenu(player, menu);
            menu.Open(player);
        });
    }

    private async Task ResetRank(int slot, string name, SteamID steamId)
    {
        var steamId2 = steamId.SteamId2.ReplaceFirstCharacter();

        await Database.ResetPlayerData(steamId2);
        Users[steamId.SteamId64] = new User
        {
            steam = steamId2,
            name = name,
            value = Config.InitialExperiencePoints
        };
        _loginTime[slot] = DateTime.UtcNow;
    }

    private async Task GetUserStats(CCSPlayerController controller, SteamID steamId)
    {
        if (!Users.TryGetValue(steamId.SteamId64, out var user)) return;

        var totalTime = TimeSpan.FromSeconds(user.playtime);
        var formattedTime =
            $"{(totalTime.Days > 0 ? $"{Localizer["days", totalTime.Days]}, " : "")}" +
            $"{(totalTime.Hours > 0 ? $"{Localizer["hours", totalTime.Hours]}, " : "")}" +
            $"{(totalTime.Minutes > 0 ? $"{Localizer["minutes", totalTime.Minutes]}, " : "")}" +
            $"{(totalTime.Seconds > 0 ? $"{Localizer["seconds", totalTime.Seconds]}" : "")}";
        var getPlayerTop = await Database.GetPlayerRankAndTotal(steamId.SteamId2);

        await Server.NextFrameAsync(() =>
        {
            if (!controller.IsValid) return;

            var headshotPercentage =
                (double)user.headshots / (user.kills + 1) * 100;
            double kdr = 0;
            if (user is { kills: > 0, deaths: > 0 })
                kdr = (double)user.kills / (user.deaths + 1);

            PrintToChat(controller,
                "-------------------------------------------------------------------");
            if (getPlayerTop != null)
                PrintToChat(controller,
                    Localizer["rank.YourPosition", getPlayerTop.PlayerRank, getPlayerTop.TotalPlayers]);

            PrintToChat(controller,
                Localizer["rank.Experience", user.value, ReplaceColorTags(GetLevelFromExperience(user.value).Name)]);
            PrintToChat(controller,
                Localizer["rank.KDA", user.kills, user.headshots, user.deaths, user.assists]);
            PrintToChat(controller, Localizer["rank.Rounds", user.round_win, user.round_lose]);
            PrintToChat(controller,
                Localizer["rank.KDR", headshotPercentage.ToString("0.00"), kdr.ToString("0.00")]);
            PrintToChat(controller, Localizer["rank.PlayTime", formattedTime]);
            PrintToChat(controller,
                "-------------------------------------------------------------------");
        });
    }

    // ------------------------------------------------------------------ config and helpers

    private string BuildConnectionString()
    {
        var dbConfig = Config.Connection;

        var builder = new MySqlConnectionStringBuilder
        {
            Database = dbConfig.Database,
            UserID = dbConfig.User,
            Password = dbConfig.Password,
            Server = dbConfig.Host,
            Port = (uint)dbConfig.Port
        };

        return builder.ConnectionString;
    }

    private Config LoadConfig()
    {
        var configPath = Path.Combine(RanksApi.CoreConfigDirectory, "ranks.json");
        if (!File.Exists(configPath)) return CreateConfig(configPath);

        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!;

        return config;
    }

    private Config CreateConfig(string configPath)
    {
        var config = new Config
        {
            TableName = "lvl_base",
            Prefix = "[ {BLUE}Ranks {DEFAULT}]",
            TeamKillAllowed = true,
            UseCommandWithoutPrefix = true,
            ShowExperienceMessages = true,
            MinPlayers = 4,
            InitialExperiencePoints = 500,
            StatisticsResetEnabled = true,
            TypeStatistics = 0,
            KillCoefficient = 1.04f,
            Events = new EventsExpSettings
            {
                EventRoundMvp = 12,
                EventPlayerDeath = new PlayerDeath
                    { Kills = 13, Deaths = -20, Assists = 5, KillingAnAlly = -10, Suicide = -15 },
                EventPlayerBomb = new Bomb { DroppedBomb = -5, DefusedBomb = 3, PickUpBomb = 3, PlantedBomb = 4 },
                EventRoundEnd = new RoundEnd { Winner = 5, Loser = -8 },
                Additionally = new Additionally
                    { Headshot = 1, Noscope = 2, Attackerblind = 1, Thrusmoke = 1, Penetrated = 2 }
            },
            Weapon = new Dictionary<string, int>
            {
                ["knife"] = 5,
                ["awp"] = 2
            },
            Ranks = new Dictionary<string, int>
            {
                { "None", 0 },
                { "Silver I", 50 },
                { "Silver II", 100 },
                { "Silver III", 150 },
                { "Silver IV", 300 },
                { "Silver Elite", 400 },
                { "Silver Elite Master", 500 },
                { "Gold Nova I", 600 },
                { "Gold Nova II", 700 },
                { "Gold Nova III", 800 },
                { "Gold Nova Master", 900 },
                { "Master Guardian I", 1000 },
                { "Master Guardian II", 1400 },
                { "Master Guardian Elite", 1600 },
                { "BigStar", 2100 },
                { "Legendary Eagle", 2600 },
                { "Legendary Eagle Master", 2900 },
                { "Supreme", 3400 },
                { "The Global Elite", 4500 }
            },
            Connection = new RankDb
            {
                Host = "HOST",
                Database = "DATABASE_NAME",
                User = "USER_NAME",
                Password = "PASSWORD",
                Port = 3306
            }
        };

        // Only ever written when the file does not exist - a mounted config is shared by every server on the node.
        File.WriteAllText(configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return config;
    }

    /// <summary>A lang/*.json string, or the fallback when the key is missing - lang files lag behind new keys.</summary>
    public string Text(string key, string fallback)
    {
        try
        {
            var value = Localizer[key];
            return value.ResourceNotFound || string.IsNullOrWhiteSpace(value.Value) ? fallback : value.Value;
        }
        catch
        {
            return fallback;
        }
    }

    public void PrintToChat(CCSPlayerController player, string message)
    {
        if (!player.IsValid) return;

        player.PrintToChat($"{ReplaceColorTags(Config.Prefix)} {message}");
    }

    private void ReplyToCommand(CCSPlayerController? player, string message)
    {
        if (player == null)
            Logger.LogInformation(message);
        else
        {
            if (!player.IsValid) return;
            PrintToChat(player, message);
        }
    }

    public void PrintToChatAll(string message)
    {
        Server.PrintToChatAll($"{ReplaceColorTags(Config.Prefix)} {message}");
    }

    private string ReplaceColorTags(string input)
    {
        return input.ReplaceColorTags();
    }

    private TimeSpan GetTotalTime(int slot)
    {
        var totalTime = DateTime.UtcNow - _loginTime[slot];

        _loginTime[slot] = DateTime.UtcNow;

        return totalTime;
    }

    /// <summary>Seconds played since the last stats save, for the menu's playtime. Does not reset the clock.</summary>
    public long SessionSeconds(int slot)
    {
        if (slot < 0 || slot >= _loginTime.Length) return 0;
        var since = _loginTime[slot];
        return since <= DateTime.MinValue.AddYears(1) ? 0 : (long)Math.Max(0, (DateTime.UtcNow - since).TotalSeconds);
    }

    /// <summary>Warmup check without a full entity scan on every shot: the rules proxy is found once per map.</summary>
    private bool IsWarmup()
    {
        if (_rules is not { IsValid: true })
            _rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();

        return _rules?.GameRules?.WarmupPeriod ?? false;
    }

    private int PlayersCount => Utilities.GetPlayers().Count(u => u is
        {
            IsBot: false, IsValid: true, TeamNum: not (0 or 1), PlayerPawn.Value: not null,
            PlayerPawn.Value.IsValid: true
        });

}

public class PlayerStats
{
    public int PlayerRank { get; init; }
    public int TotalPlayers { get; init; }
}

public class Config
{
    public required string TableName { get; init; }
    public required string Prefix { get; init; }
    public bool TeamKillAllowed { get; init; }
    public bool UseCommandWithoutPrefix { get; init; }
    public bool ShowExperienceMessages { get; init; }
    public int MinPlayers { get; init; }
    public int InitialExperiencePoints { get; init; }
    public bool StatisticsResetEnabled { get; init; }
    public int TypeStatistics { get; init; }
    public float KillCoefficient { get; init; }
    public EventsExpSettings Events { get; init; } = null!;
    public Dictionary<string, int> Weapon { get; init; } = null!;
    public Dictionary<string, int> Ranks { get; init; } = null!;
    public RankDb Connection { get; init; } = null!;

    // Next-il
    public BonusSettings Bonuses { get; init; } = new();
    public HudSettings Hud { get; init; } = new();
}
