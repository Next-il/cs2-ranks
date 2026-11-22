namespace Ranks;

// Next-il: XP is granted per event, not per part. A kill with a headshot through a wall used to be three separate
// grants - three chat lines, three bonus roundings. Now every part of one event is summed, the bonus is applied once,
// and the result is rounded once.

public enum XpPart
{
    Kill, Headshot, Wallbang, Noscope, Blind, Smoke, Weapon,
    Assist, Death, Suicide, TeamKill,
    Mvp, RoundWin, RoundLoss,
    BombDrop, BombDefuse, BombPickup, BombPlant,
}

public struct StatDelta
{
    public int Kills, Deaths, Assists, Shoots, Hits, Headshots, RoundWin, RoundLose;

    public readonly bool IsEmpty =>
        Kills == 0 && Deaths == 0 && Assists == 0 && Shoots == 0 && Hits == 0 && Headshots == 0 && RoundWin == 0 &&
        RoundLose == 0;
}

public sealed class XpGrant
{
    public readonly List<(XpPart Part, int Exp)> Parts = [];
    public StatDelta Stats;

    /// <summary>
    /// Parts are kept even at 0 XP: a headshot worth nothing in this mode still shows its icon, and still counts
    /// as a headshot in the stats.
    /// </summary>
    public XpGrant Add(XpPart part, int exp)
    {
        Parts.Add((part, exp));
        return this;
    }

    public int Base => Parts.Sum(p => p.Exp);
    public bool Has(XpPart part) => Parts.Any(p => p.Part == part);
    public bool IsEmpty => Parts.Count == 0 && Stats.IsEmpty;

    /// <summary>The part that names the line, for the events that are not a kill.</summary>
    public XpPart Lead => Parts.Count > 0 ? Parts[0].Part : XpPart.Kill;
}

/// <summary>What one grant turned into, after the bonus. This is what the feed draws.</summary>
public sealed record XpResult(XpGrant Grant, int Base, int Total, float Multiplier, bool Capped)
{
    /// <summary>
    /// Shown as boosted only when the bonus changed the number: x1.3 on 1 XP rounds back to 1, and a struck "+1"
    /// next to "+1" says nothing.
    /// </summary>
    public bool Boosted => Base > 0 && Total != Base;
}

public static class XpMath
{
    /// <summary>
    /// Half away from zero, so 6.5 is 7. Math.Round's default is banker's rounding, which would make it 6 and
    /// 2.5 would be 2.
    /// </summary>
    public static int Apply(int baseExp, float multiplier) =>
        baseExp > 0 && multiplier > 1f
            ? (int)Math.Round(baseExp * (double)multiplier, MidpointRounding.AwayFromZero)
            : baseExp;
}
