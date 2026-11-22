using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using PanoramaManager;

namespace Ranks.Hud;

/// <summary>One line of the XP feed.</summary>
internal sealed record FeedLine(
    string Amount,
    string Original = "",
    string Chip = "",
    bool Max = false,
    string Tag = "",
    string Tag2 = "",
    FeedKind Kind = FeedKind.Plain,
    IReadOnlyCollection<string>? Icons = null,
    bool Always = false);

internal enum FeedKind { Plain, Bonus, Loss, Note, RankDown }

/// <summary>
/// The XP feed and the RANK UP banner, both on one passive layout (ranks_feed.xml) that is open for every player
/// from the moment they join. /xp turns the feed lines off; the banner always shows.
///
/// <para><b>The toast model.</b> A line keeps its slot for its whole life: it slides in from off the screen with a fade
/// and a blur, stays <see cref="HudSettings.FeedSeconds"/>, and slides back out the way it came. Then its slot
/// collapses and the lines below glide up - the slot's position transition, which a collapse next to it fires.
/// Eight slots for at most five lines on screen: a new line takes the first free slot after the last one used,
/// back to the top once the stack is empty, so lines join at the bottom and the pointer almost never wraps.</para>
/// </summary>
internal sealed class XpFeed : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/ranks_feed.vxml_c";
    private const string Root = "RanksFeedRoot";
    private const int Slots = 8;
    private const int MaxOnScreen = 5;

    /// <summary>Icon panels per line, in layout order. The ids are xf{n}_{icon}.</summary>
    public static readonly string[] IconNames = ["hs", "wb", "ns", "bl", "sm", "kn", "sk"];

    private sealed class Entry
    {
        public long Id;
        public bool Leaving;
        public required FeedLine Line;
    }

    private static readonly string[] Kinds = ["plain", "bonus", "loss", "note", "rankdown"];
    private static readonly string[] StackPositions = ["pos-right", "pos-cross"];

    private sealed class Viewer
    {
        public readonly Entry?[] Slots = new Entry?[XpFeed.Slots];
        public int Next;
        public long Seq;
        public long RankUp;
        public bool RankUpShowing;
    }

    private readonly Ranks _ranks;
    private readonly PanelHandle _panel;
    private readonly ClassTracker _classes = new();
    private readonly Dictionary<int, Viewer> _viewers = [];

    public XpFeed(Ranks ranks)
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

    /// <summary>Opens the layout for a player who just joined. Safe to repeat.</summary>
    public void Attach(CCSPlayerController player)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false } || !_ranks.Config.Hud.Enabled) return;
        if (_panel.IsOpenFor(player)) return;

        _panel.Open(player);
        if (!_panel.IsOpenFor(player)) return;

        _classes.Forget(player.Slot);
        _viewers[player.Slot] = new Viewer();
        DrawEmpty(player);
    }

    public void Forget(int slot)
    {
        _panel.Close(slot);
        _classes.Forget(slot);
        _viewers.Remove(slot);
    }

    /// <summary>Map change: the timers die with the map, so whatever they were going to clean up is cleaned here.</summary>
    public void Reset()
    {
        foreach (var viewer in _viewers.Values)
        {
            Array.Clear(viewer.Slots);
            viewer.Next = 0;
            viewer.RankUpShowing = false;
        }
    }

    // ------------------------------------------------------------------ lines

    public void Push(CCSPlayerController player, FeedLine line)
    {
        if (player is not { IsValid: true, IsBot: false }) return;
        if (!line.Always && !_ranks.Prefs.Get(player.SteamID).Feed) return;

        Attach(player);
        if (!_viewers.TryGetValue(player.Slot, out var viewer)) return;

        var live = Enumerable.Range(0, Slots)
            .Where(i => viewer.Slots[i] is { Leaving: false })
            .OrderBy(i => viewer.Slots[i]!.Id)
            .ToList();
        if (live.Count >= MaxOnScreen) Leave(player, viewer, live[0]);

        if (viewer.Slots.All(s => s == null)) viewer.Next = 0;

        var slot = Enumerable.Range(viewer.Next, Slots - viewer.Next).Cast<int?>().FirstOrDefault(i => viewer.Slots[i!.Value] == null)
                   ?? Enumerable.Range(0, Slots).Cast<int?>().FirstOrDefault(i => viewer.Slots[i!.Value] == null);
        if (slot is not { } n) return; // every slot is mid-exit; dropping one line beats stacking two in one slot

        var entry = new Entry { Id = ++viewer.Seq, Line = line };
        viewer.Slots[n] = entry;
        viewer.Next = n + 1;

        Fill(player, n, line);

        // Into layout first, revealed a beat later: the slide-in needs the row to have been drawn at its off-screen
        // start before "show" moves it, and both changes in one tick would arrive together.
        _panel.SetClassFor(player, $"xf{n}", "hidden", false);
        var steamId = player.SteamID;
        Later(0.08f, steamId, p =>
        {
            if (ReferenceEquals(viewer.Slots[n], entry)) _panel.SetClassFor(p, $"xf{n}_row", "show", true);
        });
        Later(Math.Max(0.5f, _ranks.Config.Hud.FeedSeconds), steamId, p =>
        {
            if (ReferenceEquals(viewer.Slots[n], entry)) Leave(p, viewer, n);
        });
    }

    private void Leave(CCSPlayerController player, Viewer viewer, int n)
    {
        if (viewer.Slots[n] is not { Leaving: false } entry) return;

        entry.Leaving = true;
        _panel.SetClassFor(player, $"xf{n}_row", "show", false); // slides back out the way it came

        Later(0.34f, player.SteamID, p =>
        {
            if (!ReferenceEquals(viewer.Slots[n], entry)) return;
            _panel.SetClassFor(p, $"xf{n}", "hidden", true); // collapse; the lines below glide up
            viewer.Slots[n] = null;
        });
    }

    private void Fill(CCSPlayerController player, int n, FeedLine line)
    {
        var row = $"xf{n}_row";

        _panel.SetVariableFor(player, $"xf{n}_orig", line.Original);
        _panel.SetVariableFor(player, $"xf{n}_amt", line.Amount);
        _panel.SetVariableFor(player, $"xf{n}_chip", line.Chip);
        _panel.SetVariableFor(player, $"xf{n}_tag", line.Tag);
        _panel.SetVariableFor(player, $"xf{n}_tag2", line.Tag2);

        _classes.Set(_panel, player, row, line.Kind switch
        {
            FeedKind.Bonus => "bonus",
            FeedKind.Loss => "loss",
            FeedKind.Note => "note",
            FeedKind.RankDown => "rankdown",
            _ => "plain",
        }, Kinds);
        _panel.SetClassFor(player, row, "tagged", line.Tag.Length > 0);
        _panel.SetClassFor(player, row, "capped", line.Max);

        foreach (var icon in IconNames)
            _panel.SetClassFor(player, $"xf{n}_{icon}", "on", line.Icons?.Contains(icon) == true);
    }

    /// <summary>Every slot collapsed and every row off screen - the state a fresh session or a rebuilt entity needs.</summary>
    private void DrawEmpty(CCSPlayerController player)
    {
        _classes.Set(_panel, player, "xf_stack", "pos-" + FeedPosition, StackPositions);

        for (var n = 0; n < Slots; n++)
        {
            _panel.SetClassFor(player, $"xf{n}_row", "show", false);
            _panel.SetClassFor(player, $"xf{n}", "hidden", true);
            foreach (var name in new[] { "orig", "amt", "chip", "tag", "tag2" })
                _panel.SetVariableFor(player, $"xf{n}_{name}", "");
        }

        _panel.SetClassFor(player, "ru", "show", false);
        foreach (var name in new[] { "ru_name", "ru_old", "ru_new" })
            _panel.SetVariableFor(player, name, "");
    }

    private string FeedPosition =>
        _ranks.Config.Hud.FeedPosition.Trim().ToLowerInvariant() is "cross" ? "cross" : "right";

    // ------------------------------------------------------------------ rank up

    public void RankUp(CCSPlayerController player, RankInfo from, RankInfo to)
    {
        Attach(player);
        if (!_viewers.TryGetValue(player.Slot, out var viewer)) return;

        _classes.Set(_panel, player, "ru_icon", HudFormat.Icon(to.Icon), HudFormat.Icons);
        _panel.SetVariableFor(player, "ru_name", to.Name);
        _panel.SetVariableFor(player, "ru_old", from.Name);
        _panel.SetVariableFor(player, "ru_new", to.Name);

        var token = ++viewer.RankUp;
        var steamId = player.SteamID;

        if (viewer.RankUpShowing)
        {
            // Already up (two rank ups in a row): take it down and bring it back, so the pop plays again for the new
            // rank. Off and on in the same tick would be no change at all.
            _panel.SetClassFor(player, "ru", "show", false);
            Later(0.32f, steamId, p => { if (viewer.RankUp == token) _panel.SetClassFor(p, "ru", "show", true); });
        }
        else
        {
            _panel.SetClassFor(player, "ru", "show", true);
        }

        viewer.RankUpShowing = true;
        Later(Math.Max(1f, _ranks.Config.Hud.RankUpSeconds), steamId, p =>
        {
            if (viewer.RankUp != token) return;
            _panel.SetClassFor(p, "ru", "show", false);
            viewer.RankUpShowing = false;
        });
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>A timer that only fires for the same player still in the same slot.</summary>
    private void Later(float seconds, ulong steamId, Action<CCSPlayerController> action)
    {
        _ranks.AddTimer(seconds, () =>
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            if (player is { IsValid: true } && _panel.IsOpenFor(player)) action(player);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void OnEvent(PanelEvent e)
    {
        if (e.Action != PanelAction.Restored || e.Player is not { IsValid: true } player) return;

        // A round restart, a map change or a respawn. The lines still on screen stay - in Deathmatch a respawn comes
        // two seconds after the death line - so the whole stack is written again, every class in full.
        _classes.Forget(player.Slot);
        if (!_viewers.TryGetValue(player.Slot, out var viewer))
            _viewers[player.Slot] = viewer = new Viewer();

        DrawEmpty(player);
        for (var n = 0; n < Slots; n++)
        {
            if (viewer.Slots[n] is not { } entry) continue;
            Fill(player, n, entry.Line);
            _panel.SetClassFor(player, $"xf{n}", "hidden", false);
            _panel.SetClassFor(player, $"xf{n}_row", "show", !entry.Leaving);
        }
    }
}
