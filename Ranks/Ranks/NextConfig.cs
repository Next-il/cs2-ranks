namespace Ranks;

// Next-il: settings added by our fork. Every one of them has a default, so an existing ranks.json without these sections
// loads unchanged - the mounted configs are shared by every server on a node and are never written back.

public class BonusSettings
{
    /// <summary>Bonuses add up (1 + the extra part of each), and the total is capped here.</summary>
    public float MaxMultiplier { get; init; } = 3.0f;

    /// <summary>The vip.json group key holding a group's XP multiplier, read through VIP Core's API.</summary>
    public string VipFeature { get; init; } = "exp_multiplier";

    public WeekendSettings Weekend { get; init; } = new();
}

public class WeekendSettings
{
    public bool Enabled { get; init; } = true;
    public float Multiplier { get; init; } = 1.5f;

    /// <summary>English day names, any case.</summary>
    public string[] Days { get; init; } = ["Friday", "Saturday"];

    /// <summary>IANA id. The days above are judged in this zone, not the server's.</summary>
    public string TimeZone { get; init; } = "Asia/Jerusalem";
}

public class HudSettings
{
    /// <summary>Off: no HUDs at all, and XP goes back to one batched chat line per kill.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How long one XP feed line stays up.</summary>
    public float FeedSeconds { get; init; } = 3.0f;

    /// <summary>"right" (under the kill feed) or "cross" (just under the crosshair).</summary>
    public string FeedPosition { get; init; } = "right";

    public float RankUpSeconds { get; init; } = 3.6f;

    /// <summary>The menu header tag. Empty: taken from TableName, "ranks_retakes" -> "RETAKES".</summary>
    public string ModeLabel { get; init; } = "";

    /// <summary>The per-player settings table, shared by every mode on the same database.</summary>
    public string SettingsTable { get; init; } = "ranks_hud_settings";

    public HudDefaults Defaults { get; init; } = new();
}

public class HudDefaults
{
    public bool Feed { get; init; } = true;

    /// <summary>"off", "dead" or "always".</summary>
    public string Panel { get; init; } = "dead";

    /// <summary>"radar", "right" or "bottom".</summary>
    public string Placement { get; init; } = "radar";

    /// <summary>"s", "m" or "l".</summary>
    public string Size { get; init; } = "m";
}
