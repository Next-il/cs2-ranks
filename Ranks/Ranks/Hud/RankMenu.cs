using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using PanoramaManager;

namespace Ranks.Hud;

/// <summary>
/// /rank: Overview, Top players, All ranks and Settings, on one mouse-driven layout (ranks_menu.xml). /top opens the
/// same menu on Top players. Every page is drawn on open, so switching tabs is only a class change.
/// </summary>
internal sealed class RankMenu : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/ranks_menu.vxml_c";
    private const string Root = "RanksMenuRoot";

    public const int TabOverview = 0, TabTop = 1, TabRanks = 2, TabSettings = 3;
    private const int Tabs = 4;
    private const int TopRows = 10;
    private const int RankTiles = 24;
    private const int TilesPerRow = 6;

    private static readonly string[] TabSteps = ["at-0", "at-1", "at-2", "at-3"];

    private sealed class Viewer
    {
        public int Tab;
        public long Token;
    }

    private readonly Ranks _ranks;
    private readonly PanelHandle _panel;
    private readonly ClassTracker _classes = new();
    private readonly Dictionary<int, Viewer> _viewers = [];

    private List<TopRow> _top = [];
    private DateTime _topAt = DateTime.MinValue;

    public RankMenu(Ranks ranks)
    {
        _ranks = ranks;
        _panel = Panorama.Spawn(Layout, new LayoutContract
        {
            RootPanelId = Root,
            RevealClass = "show",
            CloseButtonId = "rm_close",
            RowCount = 0,
            CaptureInput = true,
            HideHud = HideHudFlags.None,
            SharedText = false,
        });
        _panel.OnEvent += OnEvent;
    }

    public void Dispose() => _panel.Dispose();

    public void Open(CCSPlayerController player, int tab)
    {
        if (player is not { IsValid: true, IsBot: false } || !_ranks.Users.ContainsKey(player.SteamID)) return;

        _panel.Open(player);
        if (!_panel.IsOpenFor(player)) return;

        _classes.Forget(player.Slot);
        var viewer = _viewers[player.Slot] = new Viewer { Tab = Math.Clamp(tab, 0, Tabs - 1) };
        DrawAll(player, viewer);
        Fetch(player, viewer);
    }

    public void Forget(int slot)
    {
        _panel.Close(slot);
        _classes.Forget(slot);
        _viewers.Remove(slot);
    }

    /// <summary>Redraws an open menu after the player's XP or bonuses changed.</summary>
    public void Refresh(CCSPlayerController player)
    {
        if (_panel.IsOpenFor(player) && _viewers.TryGetValue(player.Slot, out var viewer))
        {
            DrawOverview(player);
            DrawRanks(player);
            DrawTabs(player, viewer);
        }
    }

    // ------------------------------------------------------------------ drawing

    private void DrawAll(CCSPlayerController player, Viewer viewer)
    {
        _panel.SetVariableFor(player, "rm_tag", ModeLabel());
        DrawTabs(player, viewer);
        DrawOverview(player);
        // Position, filled in by Fetch once the database answers.
        _panel.SetVariableFor(player, "ov_pos", "-");
        _panel.SetVariableFor(player, "ov_total", "-");
        DrawTop(player, loading: true);
        DrawRanks(player);
        DrawSettings(player);
    }

    private void DrawTabs(CCSPlayerController player, Viewer viewer)
    {
        for (var i = 0; i < Tabs; i++)
        {
            _panel.SetClassFor(player, $"rt_{i}", "active", i == viewer.Tab);
            _panel.SetClassFor(player, $"pg_{i}", "active", i == viewer.Tab);
        }

        _classes.Set(_panel, player, "rm_bar", TabSteps[viewer.Tab], TabSteps);
    }

    private void DrawOverview(CCSPlayerController player)
    {
        if (!_ranks.Users.TryGetValue(player.SteamID, out var user)) return;

        var rank = _ranks.RankFor(user.value);
        var bonus = _ranks.Bonuses.For(player);

        _classes.Set(_panel, player, "ov_icon", HudFormat.Icon(rank.Icon), HudFormat.Icons);
        _panel.SetVariableFor(player, "ov_name", rank.Name);
        _panel.SetVariableFor(player, "ov_chip", "XP " + HudFormat.Multiplier(bonus.Total));
        _panel.SetClassFor(player, "ov_chip", "hidden", !bonus.Any);
        _panel.SetVariableFor(player, "ov_xp", HudFormat.Number(user.value) + " XP");

        if (rank.Next is { } next)
        {
            _panel.SetVariableFor(player, "ov_togo", $"{HudFormat.Number(next.Value - user.value)} XP to {next.Name}");
            _panel.SetVariableFor(player, "ov_from", $"{rank.Name} · {HudFormat.Number(rank.Value)}");
            _panel.SetVariableFor(player, "ov_to", $"{next.Name} · {HudFormat.Number(next.Value)}");
        }
        else
        {
            _panel.SetVariableFor(player, "ov_togo", _ranks.Text("hud.top_rank", "Top rank reached"));
            _panel.SetVariableFor(player, "ov_from", $"{rank.Name} · {HudFormat.Number(rank.Value)}");
            _panel.SetVariableFor(player, "ov_to", _ranks.Text("hud.max_rank", "Max rank"));
        }

        _classes.Set(_panel, player, "ov_fill", HudFormat.Step(rank.Progress), HudFormat.Steps);

        var kd = user.kills / (double)Math.Max(1, user.deaths);
        var hs = user.kills > 0 ? user.headshots * 100.0 / user.kills : 0;
        var play = TimeSpan.FromSeconds(user.playtime + _ranks.SessionSeconds(player.Slot));

        _panel.SetVariableFor(player, "st_kills", HudFormat.Number(user.kills));
        _panel.SetVariableFor(player, "st_deaths", HudFormat.Number(user.deaths));
        _panel.SetVariableFor(player, "st_kd", kd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        _panel.SetVariableFor(player, "st_hs", Math.Round(hs).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _panel.SetVariableFor(player, "st_assists", HudFormat.Number(user.assists));
        _panel.SetVariableFor(player, "st_rounds", HudFormat.Number(user.round_win));
        _panel.SetVariableFor(player, "st_play_h", HudFormat.Number((long)play.TotalHours));
        _panel.SetVariableFor(player, "st_play_m", play.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Bonus tiles, the same sources as the rank panel.
        var notActive = _ranks.Text("hud.not_active", "Not active");
        _panel.SetClassFor(player, "ob_vip", "on", bonus.Vip > 1f);
        _panel.SetVariableFor(player, "ob_vip_v", bonus.Vip > 1f ? HudFormat.Multiplier(bonus.Vip) : notActive);
        _panel.SetClassFor(player, "ob_name", "on", bonus.NameReward);
        _panel.SetVariableFor(player, "ob_name_v", bonus.NameReward ? HudFormat.Multiplier(bonus.NameMultiplier) : notActive);
        _panel.SetClassFor(player, "ob_wknd", "on", bonus.Weekend);
        _panel.SetVariableFor(player, "ob_wknd_l", $"Weekend {HudFormat.Multiplier(_ranks.Config.Bonuses.Weekend.Multiplier)}");
        _panel.SetVariableFor(player, "ob_wknd_v", bonus.Weekend ? _ranks.Text("hud.active", "Active") : notActive);
        _panel.SetClassFor(player, "ob_total", "on", bonus.Any);
        _panel.SetVariableFor(player, "ob_total", HudFormat.Multiplier(bonus.Total) + (bonus.Capped ? " MAX" : ""));

    }

    private void DrawRanks(CCSPlayerController player)
    {
        if (!_ranks.Users.TryGetValue(player.SteamID, out var user)) return;
        var rank = _ranks.RankFor(user.value);

        for (var i = 0; i < RankTiles; i++)
        {
            var tile = $"ar{i}";
            if (i >= rank.All.Count)
            {
                _panel.SetClassFor(player, tile, "hidden", true);
                _panel.SetVariableFor(player, $"{tile}_n", "");
                _panel.SetVariableFor(player, $"{tile}_x", "");
                continue;
            }

            var (name, value) = rank.All[i];
            _panel.SetClassFor(player, tile, "hidden", false);
            _panel.SetClassFor(player, tile, "cur", i == rank.Index);
            _panel.SetClassFor(player, tile, "locked", i > rank.Index);
            _classes.Set(_panel, player, $"{tile}_icon", HudFormat.Icon(i), HudFormat.Icons);
            _panel.SetVariableFor(player, $"{tile}_n", name);
            _panel.SetVariableFor(player, $"{tile}_x", HudFormat.Number(value) + " XP");
        }

        for (var r = 0; r < RankTiles / TilesPerRow; r++)
            _panel.SetClassFor(player, $"ar_row{r}", "hidden", r * TilesPerRow >= rank.All.Count);
    }

    private void DrawTop(CCSPlayerController player, bool loading, int position = 0, int total = 0)
    {
        if (!_ranks.Users.TryGetValue(player.SteamID, out var user)) return;

        _panel.SetVariableFor(player, "tp_status", loading ? _ranks.Text("hud.loading", "Loading...") : "");
        _panel.SetClassFor(player, "tp_status", "hidden", !loading);

        var me = new SteamID(player.SteamID).SteamId2.ReplaceFirstCharacter();
        var meInList = false;

        for (var i = 0; i < TopRows; i++)
        {
            var row = $"tp{i}";
            if (loading || i >= _top.Count)
            {
                _panel.SetClassFor(player, row, "hidden", true);
                foreach (var col in new[] { "pos", "name", "kd", "xp" }) _panel.SetVariableFor(player, $"{row}_{col}", "");
                continue;
            }

            var top = _top[i];
            var isMe = top.Steam == me;
            meInList |= isMe;

            _panel.SetClassFor(player, row, "hidden", false);
            _panel.SetClassFor(player, row, "me", isMe);
            _panel.SetClassFor(player, row, "top3", i < 3);
            _classes.Set(_panel, player, $"{row}_icon", HudFormat.Icon(_ranks.RankFor(top.Value).Icon), HudFormat.Icons);
            _panel.SetVariableFor(player, $"{row}_pos", (i + 1).ToString());
            _panel.SetVariableFor(player, $"{row}_name", isMe ? user.name : top.Name);
            _panel.SetVariableFor(player, $"{row}_kd", Kd(top.Kills, top.Deaths));
            _panel.SetVariableFor(player, $"{row}_xp", HudFormat.Number(top.Value));
        }

        // Outside the top 10 the player's own place is pinned under the list; inside it, their row is marked instead.
        var pin = !loading && !meInList;
        _panel.SetClassFor(player, "tp_me", "hidden", !pin);
        _classes.Set(_panel, player, "tp_me_icon", HudFormat.Icon(_ranks.RankFor(user.value).Icon), HudFormat.Icons);
        _panel.SetVariableFor(player, "tp_me_pos", position > 0 ? HudFormat.Number(position) : "-");
        _panel.SetVariableFor(player, "tp_me_name", player.PlayerName);
        _panel.SetVariableFor(player, "tp_me_kd", Kd(user.kills, user.deaths));
        _panel.SetVariableFor(player, "tp_me_xp", HudFormat.Number(user.value));

        if (!loading)
        {
            _panel.SetVariableFor(player, "ov_pos", position > 0 ? HudFormat.Number(position) : "-");
            _panel.SetVariableFor(player, "ov_total", total > 0 ? HudFormat.Number(total) : "-");
        }
    }

    private void DrawSettings(CCSPlayerController player)
    {
        var prefs = _ranks.Prefs.Get(player.SteamID);

        _panel.SetClassFor(player, "se_feed_on", "on", prefs.Feed);
        _panel.SetClassFor(player, "se_feed_off", "on", !prefs.Feed);

        foreach (var mode in Enum.GetValues<PanelMode>())
            _panel.SetClassFor(player, $"se_mode_{HudPrefs.Name(mode)}", "on", prefs.Mode == mode);
        foreach (var placement in Enum.GetValues<PanelPlacement>())
            _panel.SetClassFor(player, $"se_pos_{HudPrefs.Name(placement)}", "on", prefs.Placement == placement);
        foreach (var size in Enum.GetValues<PanelSize>())
            _panel.SetClassFor(player, $"se_size_{HudPrefs.Name(size)}", "on", prefs.Size == size);

        var off = prefs.Mode == PanelMode.Off;
        _panel.SetClassFor(player, "se_pos_opts", "disabled", off);
        _panel.SetClassFor(player, "se_size_opts", "disabled", off);

        _classes.Set(_panel, player, "mini_blk", "pos-" + HudPrefs.Name(prefs.Placement), HudFormat.Placements);
        _classes.Set(_panel, player, "mini_size", "size-" + HudPrefs.Name(prefs.Size), HudFormat.Sizes);
        _panel.SetClassFor(player, "mini_blk", "off", off);
        _panel.SetVariableFor(player, "mini_lbl", prefs.Mode switch
        {
            PanelMode.Off => _ranks.Text("hud.panel_off", "Off"),
            PanelMode.Always => _ranks.Text("hud.panel_always", "Always"),
            _ => _ranks.Text("hud.panel_dead", "While dead"),
        });
    }

    private static string Kd(int kills, int deaths) =>
        (kills / (double)Math.Max(1, deaths)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private string ModeLabel()
    {
        var label = _ranks.Config.Hud.ModeLabel;
        if (!string.IsNullOrWhiteSpace(label)) return label.ToUpperInvariant();

        var table = _ranks.Config.TableName;
        if (table.StartsWith("ranks_", StringComparison.OrdinalIgnoreCase)) table = table[6..];
        return table.Replace('_', ' ').ToUpperInvariant();
    }

    // ------------------------------------------------------------------ data

    public sealed record TopRow(string Steam, string Name, int Value, int Kills, int Deaths);

    private void Fetch(CCSPlayerController player, Viewer viewer)
    {
        var token = ++viewer.Token;
        var slot = player.Slot;
        var steamId64 = player.SteamID;
        var steamId2 = new SteamID(player.SteamID).SteamId2;
        var stale = (DateTime.UtcNow - _topAt).TotalSeconds > 60;

        Task.Run(async () =>
        {
            var top = stale ? await _ranks.Database.GetTopForMenu(TopRows) : null;
            var place = await _ranks.Database.GetPlayerRankAndTotal(steamId2);

            await Server.NextFrameAsync(() =>
            {
                if (top != null)
                {
                    _top = top;
                    _topAt = DateTime.UtcNow;
                }

                var current = Utilities.GetPlayerFromSlot(slot);
                if (current is not { IsValid: true } || current.SteamID != steamId64 || !_panel.IsOpenFor(current)) return;
                if (!_viewers.TryGetValue(slot, out var v) || v.Token != token) return;

                DrawTop(current, loading: false, place?.PlayerRank ?? 0, place?.TotalPlayers ?? 0);
            });
        });
    }

    // ------------------------------------------------------------------ input

    private void OnEvent(PanelEvent e)
    {
        if (e.Player is not { IsValid: true } player) return;

        switch (e.Action)
        {
            case PanelAction.Close:
                _classes.Forget(player.Slot);
                _viewers.Remove(player.Slot);
                return;

            case PanelAction.Restored:
                if (!_viewers.TryGetValue(player.Slot, out var restored))
                {
                    _panel.Close(player);
                    return;
                }

                _classes.Forget(player.Slot);
                DrawAll(player, restored);
                Fetch(player, restored);
                return;

            case PanelAction.Button:
                if (!_viewers.TryGetValue(player.Slot, out var viewer)) return;
                Click(player, viewer, e.ElementId);
                return;
        }
    }

    private void Click(CCSPlayerController player, Viewer viewer, string id)
    {
        if (id.StartsWith("rt_") && int.TryParse(id.AsSpan(3), out var tab) && tab is >= 0 and < Tabs)
        {
            viewer.Tab = tab;
            DrawTabs(player, viewer);
            return;
        }

        if (!id.StartsWith("se_")) return;

        var prefs = _ranks.Prefs.Update(player.SteamID, p =>
        {
            switch (id)
            {
                case "se_feed_on": p.Feed = true; break;
                case "se_feed_off": p.Feed = false; break;
                case "se_mode_off": p.Mode = PanelMode.Off; break;
                case "se_mode_dead": p.Mode = PanelMode.Dead; break;
                case "se_mode_always": p.Mode = PanelMode.Always; break;
                case "se_pos_radar": p.Placement = PanelPlacement.Radar; break;
                case "se_pos_right": p.Placement = PanelPlacement.Right; break;
                case "se_pos_bottom": p.Placement = PanelPlacement.Bottom; break;
                case "se_size_s": p.Size = PanelSize.S; break;
                case "se_size_m": p.Size = PanelSize.M; break;
                case "se_size_l": p.Size = PanelSize.L; break;
            }
        });

        DrawSettings(player);
        _ranks.HudPrefsChanged(player, prefs);
    }
}
