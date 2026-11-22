using System.Globalization;
using CounterStrikeSharp.API.Core;
using PanoramaManager;

namespace Ranks.Hud;

/// <summary>
/// One-of-many classes per viewer: a rank icon (sg-0..sg-23), a bar step (p0..p50), a placement. The layout lists
/// every option, and if two are on at once the stylesheet paints whichever it defines last - so the old one has to
/// come off before the new one goes on, which means knowing which one is on.
///
/// <para>Forget a viewer whenever their session is reset: <see cref="PanelHandle.Open"/> takes every per-viewer class
/// back off, and a stale entry here would then skip writing a class that is no longer there.</para>
/// </summary>
internal sealed class ClassTracker
{
    private readonly Dictionary<(int Slot, string Panel), string?> _current = new();

    /// <summary>
    /// Puts <paramref name="className"/> on (null: none of them) and takes the other options off. While the tracker
    /// knows what is on, only the difference is written. After <see cref="Forget"/> it does not, and then every option
    /// is written: a round restart keeps the entity and whatever classes it wore, a map change builds a new one with
    /// none, and only a full write is right for both.
    /// </summary>
    public void Set(PanelHandle panel, CCSPlayerController player, string panelId, string? className,
        IReadOnlyList<string> options)
    {
        var key = (player.Slot, panelId);
        if (_current.TryGetValue(key, out var old))
        {
            if (old == className) return;
            if (old != null) panel.SetClassFor(player, panelId, old, false);
        }
        else
        {
            foreach (var option in options)
                if (option != className) panel.SetClassFor(player, panelId, option, false);
        }

        if (className != null) panel.SetClassFor(player, panelId, className, true);
        _current[key] = className;
    }

    public void Forget(int slot)
    {
        foreach (var key in _current.Keys.Where(k => k.Slot == slot).ToList())
            _current.Remove(key);
    }
}

internal static class HudFormat
{
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"x1.3", "x2.0", "x3.0".</summary>
    public static string Multiplier(float value) =>
        "x" + (Math.Round(value * 10f) / 10f).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The bar ladder the layouts carry: p0..p50, 2% a step.</summary>
    public static string Step(double fraction) =>
        "p" + Math.Clamp((int)Math.Floor(Math.Clamp(fraction, 0, 1) * 50), 0, 50);

    /// <summary>Rank art: CS2's skillgroup0..18 plus the workshop addon's 19..23.</summary>
    public const int MaxIcon = 23;

    public static readonly string[] Icons = Enumerable.Range(0, MaxIcon + 1).Select(i => "sg-" + i).ToArray();
    public static readonly string[] Steps = Enumerable.Range(0, 51).Select(i => "p" + i).ToArray();
    public static readonly string[] Placements = ["pos-radar", "pos-right", "pos-bottom"];
    public static readonly string[] Sizes = ["size-s", "size-m", "size-l"];

    public static string Icon(int index) => "sg-" + Math.Clamp(index, 0, MaxIcon);

    /// <summary>"−2": a real minus sign, the hyphen reads as a dash at this size.</summary>
    public static string Signed(int value) => value >= 0 ? "+" + value : "−" + Math.Abs(value);
}

/// <summary>Where a player stands on the current mode's ladder.</summary>
public sealed record RankInfo(
    int Index,
    string Name,
    int Value,
    (string Name, int Value)? Next,
    double Progress,
    IReadOnlyList<(string Name, int Value)> All)
{
    /// <summary>
    /// The skill-group art, the same mapping the scoreboard's Ranks_FakeRank uses: rank N in ranks.json is
    /// skillgroup N-1, so the 1st entry is skillgroup0 (unranked) and Global is skillgroup18.
    /// </summary>
    public int Icon => Math.Clamp(Index, 0, HudFormat.MaxIcon);
}
