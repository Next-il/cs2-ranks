using System.Globalization;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using VipCoreApi;

namespace Ranks;

// Next-il: the XP bonuses, moved here from the VIP_ExperienceMultiplier module of our VIP Core fork.
//
// Bonuses ADD: total = 1 + sum(m - 1). VIP x1.3 with the name reward x1.5 and the weekend x1.5 is x2.3, not x2.925.
// The total is capped by Bonuses.MaxMultiplier, and lost XP is never multiplied.

public sealed record BonusState(
    float Vip, string VipGroup,
    bool NameReward, float NameMultiplier,
    bool Weekend, float WeekendMultiplier,
    float Raw, float Total, bool Capped)
{
    public bool Any => Total > 1f;
}

public sealed class BonusService
{
    private readonly Ranks _ranks;
    private TimeZoneInfo? _zone;
    private string _zoneId = "";
    private bool _zoneWarned;

    public BonusService(Ranks ranks) => _ranks = ranks;

    public BonusState For(CCSPlayerController player)
    {
        var settings = _ranks.Config.Bonuses;

        var (vip, group) = VipBridge.Multiplier(player, settings.VipFeature);

        var nameReward = false;
        var nameMultiplier = Math.Max(1f, _ranks.NameRewardMultiplier.Value);
        var name = _ranks.NameRewardName.Value?.Trim() ?? "";
        if (name.Length > 0 && nameMultiplier > 1f)
            nameReward = player.PlayerName.Contains(name, StringComparison.OrdinalIgnoreCase);

        var weekendMultiplier = Math.Max(1f, settings.Weekend.Multiplier);
        var weekend = settings.Weekend.Enabled && weekendMultiplier > 1f && IsWeekend();

        var raw = 1f + (vip - 1f) + (nameReward ? nameMultiplier - 1f : 0f) + (weekend ? weekendMultiplier - 1f : 0f);
        var max = Math.Max(1f, settings.MaxMultiplier);
        var total = Math.Min(raw, max);

        return new BonusState(vip, group, nameReward, nameMultiplier, weekend, weekendMultiplier, raw, total,
            raw >= max && raw > 1f);
    }

    public bool IsWeekend()
    {
        var weekend = _ranks.Config.Bonuses.Weekend;
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone(weekend.TimeZone));

        foreach (var day in weekend.Days)
            if (Enum.TryParse<DayOfWeek>(day, true, out var parsed) && parsed == now.DayOfWeek)
                return true;

        return false;
    }

    /// <summary>"Fri - Sat", for the panel's weekend row.</summary>
    public string WeekendDays()
    {
        var days = _ranks.Config.Bonuses.Weekend.Days
            .Select(d => Enum.TryParse<DayOfWeek>(d, true, out var p) ? p : (DayOfWeek?)null)
            .Where(d => d.HasValue)
            .Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d!.Value))
            .ToList();

        return days.Count switch
        {
            0 => "",
            1 => days[0],
            _ => $"{days[0]} - {days[^1]}",
        };
    }

    private TimeZoneInfo Zone(string id)
    {
        if (_zone != null && _zoneId == id) return _zone;

        _zoneId = id;
        try
        {
            _zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e)
        {
            if (!_zoneWarned)
                _ranks.Logger.LogWarning("Weekend bonus: time zone '{id}' not found ({error}), using UTC", id, e.Message);
            _zoneWarned = true;
            _zone = TimeZoneInfo.Utc;
        }

        return _zone;
    }
}

/// <summary>
/// VIP Core is optional. Every touch of its types sits in a NoInlining method behind a try, so a server without
/// VipCoreApi in shared/ fails the lookup once, quietly, instead of failing to load this plugin.
/// </summary>
internal static class VipBridge
{
    private static object? _api;
    private static bool _failed;

    public static void Resolve(ILogger logger)
    {
        _api = null;
        try
        {
            _api = Lookup();
        }
        catch (Exception e)
        {
            if (!_failed) logger.LogInformation("VIP Core API not available, the VIP XP bonus is off: {error}", e.Message);
            _failed = true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? Lookup() => IVipCoreApi.Capability.Get();

    /// <summary>The player's group multiplier (1 when not a VIP) and the group name.</summary>
    public static (float Multiplier, string Group) Multiplier(CCSPlayerController player, string feature)
    {
        if (_api == null) return (1f, "");

        try
        {
            return Read(_api, player, feature);
        }
        catch
        {
            return (1f, "");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (float, string) Read(object api, CCSPlayerController player, string feature)
    {
        var vip = (IVipCoreApi)api;
        if (!vip.IsPlayerVip(player)) return (1f, "");

        var value = vip.GetFeatureValue<float>(player, feature);
        return (value > 1f ? value : 1f, vip.GetPlayerVipGroup(player) ?? "");
    }
}
