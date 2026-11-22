using CounterStrikeSharp.API.Core;
using PanoramaManager;

namespace Ranks.Hud;

/// <summary>
/// The rank card: current rank, the bar to the next one, and the active XP bonuses. Shown while the player is dead,
/// or all the time if they picked "always" in /rank. Passive - it never takes the mouse.
///
/// <para>Layout: ranks_panel.xml. Every text slot is written per viewer (SharedText is off), so a variable this class
/// forgets to write shows its raw {s:name} token. Static labels are literal text in the layout.</para>
/// </summary>
internal sealed class RankPanel : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/ranks_panel.vxml_c";
    private const string Root = "RanksPanelRoot";

    private readonly Ranks _ranks;
    private readonly PanelHandle _panel;
    private readonly ClassTracker _classes = new();

    /// <summary>Which of the two swap classes is on each viewer's current icon - see <see cref="RankChanged"/>.</summary>
    private readonly Dictionary<int, bool> _swap = [];

    public RankPanel(Ranks ranks)
    {
        _ranks = ranks;
        _panel = Panorama.Spawn(Layout, new LayoutContract
        {
            RootPanelId = Root,
            RevealClass = "show",
            RowCount = 0,
            CaptureInput = false,
            HideHud = HideHudFlags.None,
            SharedText = false,
        });
        _panel.OnEvent += OnEvent;
    }

    public void Dispose() => _panel.Dispose();

    /// <summary>Opens or closes the card to match the player's choice and whether they are alive. Cheap to repeat.</summary>
    public void Sync(CCSPlayerController player)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false }) return;

        var want = ShouldShow(player);
        var open = _panel.IsOpenFor(player);

        if (want && !open)
        {
            _panel.Open(player);
            if (!_panel.IsOpenFor(player)) return;
            Reset(player.Slot);
            Draw(player);
        }
        else if (want)
        {
            Draw(player);
        }
        else if (open)
        {
            _panel.Close(player);
            Reset(player.Slot);
        }
    }

    /// <summary>Redraws an open card after an XP change, a bonus change or a settings change.</summary>
    public void Refresh(CCSPlayerController player)
    {
        if (player is { IsValid: true } && _panel.IsOpenFor(player)) Draw(player);
    }

    /// <summary>Flashes the current icon as it swaps to the new rank. A no-op when the card is down.</summary>
    public void RankChanged(CCSPlayerController player)
    {
        if (!_panel.IsOpenFor(player)) return;

        Draw(player);

        // Two class names with the same keyframes, alternated: taking one class off and putting it back in the same
        // tick is no change at all on the wire, so the animation would never restart.
        var a = _swap.GetValueOrDefault(player.Slot);
        _panel.SetClassFor(player, "rp_icon_cur", a ? "swap-a" : "swap-b", false);
        _panel.SetClassFor(player, "rp_icon_cur", a ? "swap-b" : "swap-a", true);
        _swap[player.Slot] = !a;
    }

    public void Forget(int slot)
    {
        _panel.Close(slot);
        Reset(slot);
    }

    private void Reset(int slot)
    {
        _classes.Forget(slot);
        _swap.Remove(slot);
    }

    private bool ShouldShow(CCSPlayerController player)
    {
        if (!_ranks.Config.Hud.Enabled || !_ranks.Users.ContainsKey(player.SteamID)) return false;

        return _ranks.Prefs.Get(player.SteamID).Mode switch
        {
            PanelMode.Always => true,
            PanelMode.Dead => !player.PawnIsAlive,
            _ => false,
        };
    }

    private void Draw(CCSPlayerController player)
    {
        if (!_ranks.Users.TryGetValue(player.SteamID, out var user)) return;

        var prefs = _ranks.Prefs.Get(player.SteamID);
        var rank = _ranks.RankFor(user.value);
        var bonus = _ranks.Bonuses.For(player);

        Placement(player, prefs);

        _panel.SetClassFor(player, Root, "maxed", rank.Next == null);
        _classes.Set(_panel, player, "rp_icon_cur", HudFormat.Icon(rank.Icon), HudFormat.Icons);
        _classes.Set(_panel, player, "rp_icon_next", rank.Next == null ? null : HudFormat.Icon(rank.Icon + 1), HudFormat.Icons);
        _classes.Set(_panel, player, "rp_fill", HudFormat.Step(rank.Progress), HudFormat.Steps);

        _panel.SetVariableFor(player, "rp_cur", rank.Name);
        _panel.SetVariableFor(player, "rp_next", rank.Next?.Name ?? Text("hud.max_rank", "Max rank"));
        _panel.SetVariableFor(player, "rp_xp_now", HudFormat.Number(user.value));
        _panel.SetVariableFor(player, "rp_xp_need",
            rank.Next is { } next ? $"/ {HudFormat.Number(next.Value)} XP" : "XP");

        // Bonus list. A row is "on" when that source is active; the total is top right, MAX when capped.
        _panel.SetClassFor(player, "rp_total", "on", bonus.Any);
        _panel.SetClassFor(player, "rp_total_max", "hidden", !bonus.Capped);
        _panel.SetVariableFor(player, "rp_total", HudFormat.Multiplier(bonus.Total));

        var vip = bonus.Vip > 1f;
        _panel.SetClassFor(player, "rp_b_vip", "on", vip);
        _panel.SetVariableFor(player, "rp_vip", vip ? HudFormat.Multiplier(bonus.Vip) : Text("hud.not_active", "Not active"));
        // The group name only when it says more than "VIP".
        _panel.SetVariableFor(player, "rp_vip_hint",
            vip && !bonus.VipGroup.Equals("VIP", StringComparison.OrdinalIgnoreCase) ? bonus.VipGroup.ToUpperInvariant() : "");

        _panel.SetClassFor(player, "rp_b_name", "on", bonus.NameReward);
        _panel.SetVariableFor(player, "rp_name",
            bonus.NameReward ? HudFormat.Multiplier(bonus.NameMultiplier) : Text("hud.not_active", "Not active"));
        _panel.SetVariableFor(player, "rp_name_hint",
            bonus.NameReward ? "" : $"add \"{_ranks.NameRewardName.Value?.Trim()}\" to your name");

        _panel.SetClassFor(player, "rp_b_wknd", "on", bonus.Weekend);
        _panel.SetVariableFor(player, "rp_wknd", bonus.Weekend ? Text("hud.active", "Active") : Text("hud.not_active", "Not active"));
        _panel.SetVariableFor(player, "rp_wknd_label", $"Weekend {HudFormat.Multiplier(_ranks.Config.Bonuses.Weekend.Multiplier)}");
        _panel.SetVariableFor(player, "rp_wknd_hint", _ranks.Bonuses.WeekendDays());

        _panel.SetVariableFor(player, "rp_foot",
            prefs.Feed ? Text("hud.xp_off", "turns the XP feed off") : Text("hud.xp_on", "turns the XP feed on"));
    }

    private void Placement(CCSPlayerController player, HudPrefs prefs)
    {
        _classes.Set(_panel, player, Root, "pos-" + HudPrefs.Name(prefs.Placement), HudFormat.Placements);
        // Size lives on the inner scaler so it never fights the placement's slide-in transform.
        _classes.Set(_panel, player, "rp_scale", "size-" + HudPrefs.Name(prefs.Size), HudFormat.Sizes);
    }

    private void OnEvent(PanelEvent e)
    {
        if (e.Action != PanelAction.Restored || e.Player is not { IsValid: true } player) return;

        // A round restart or a respawn: every per-viewer write is ours to redraw, all of it, since the entity may or
        // may not still wear what it had. If the player should no longer see the card (they respawned), close it.
        Reset(player.Slot);
        if (ShouldShow(player)) Draw(player);
        else
        {
            _panel.Close(player);
            Reset(player.Slot);
        }
    }

    private string Text(string key, string fallback) => _ranks.Text(key, fallback);
}
