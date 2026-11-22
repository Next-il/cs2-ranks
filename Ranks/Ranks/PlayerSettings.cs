using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Ranks;

// Next-il: each player's HUD choices, kept in MySQL so they follow the player to every server and mode on the same
// database. The table is created by hand from ranks-hud-settings.sql (we never run DDL from a plugin on prod); until
// it exists every player simply gets the defaults and nothing is saved.

public enum PanelMode : byte { Off = 0, Dead = 1, Always = 2 }
public enum PanelPlacement : byte { Radar = 0, Right = 1, Bottom = 2 }
public enum PanelSize : byte { S = 0, M = 1, L = 2 }

public sealed class HudPrefs
{
    public bool Feed = true;
    public PanelMode Mode = PanelMode.Dead;
    public PanelPlacement Placement = PanelPlacement.Radar;
    public PanelSize Size = PanelSize.M;

    public HudPrefs Clone() => (HudPrefs)MemberwiseClone();

    public static string Name(PanelMode m) => m switch { PanelMode.Off => "off", PanelMode.Always => "always", _ => "dead" };
    public static string Name(PanelPlacement p) => p switch { PanelPlacement.Right => "right", PanelPlacement.Bottom => "bottom", _ => "radar" };
    public static string Name(PanelSize s) => s switch { PanelSize.S => "s", PanelSize.L => "l", _ => "m" };

    public static HudPrefs FromDefaults(HudDefaults d) => new()
    {
        Feed = d.Feed,
        Mode = d.Panel.Trim().ToLowerInvariant() switch { "off" => PanelMode.Off, "always" => PanelMode.Always, _ => PanelMode.Dead },
        Placement = d.Placement.Trim().ToLowerInvariant() switch { "right" => PanelPlacement.Right, "bottom" => PanelPlacement.Bottom, _ => PanelPlacement.Radar },
        Size = d.Size.Trim().ToLowerInvariant() switch { "s" => PanelSize.S, "l" => PanelSize.L, _ => PanelSize.M },
    };
}

public sealed class HudPrefsStore
{
    private const int NoSuchTable = 1146;

    private readonly Ranks _ranks;
    private readonly ConcurrentDictionary<ulong, HudPrefs> _prefs = new();
    private bool _missingTable;

    public HudPrefsStore(Ranks ranks) => _ranks = ranks;

    private string Table => _ranks.Config.Hud.SettingsTable;
    private HudPrefs Defaults => HudPrefs.FromDefaults(_ranks.Config.Hud.Defaults);

    /// <summary>The player's prefs, or the defaults while they load or when they never saved any.</summary>
    public HudPrefs Get(ulong steamId) => _prefs.TryGetValue(steamId, out var p) ? p : Defaults;

    public void Forget(ulong steamId) => _prefs.TryRemove(steamId, out _);

    public async Task<HudPrefs> LoadAsync(ulong steamId)
    {
        var prefs = Defaults;
        if (!_missingTable)
        {
            try
            {
                await using var connection = new MySqlConnection(_ranks.DbConnectionString);
                await connection.OpenAsync();
                // CAST AS SIGNED: MySqlConnector hands TINYINT(1) back as bool and TINYINT as sbyte, so every column
                // comes back as one plain integer type instead.
                var row = await connection.QueryFirstOrDefaultAsync<PrefsRow>(
                    $@"SELECT CAST(`feed` AS SIGNED) AS Feed, CAST(`panel_mode` AS SIGNED) AS Mode,
                              CAST(`placement` AS SIGNED) AS Placement, CAST(`size` AS SIGNED) AS Size
                       FROM `{Table}` WHERE `steamid64` = @id",
                    new { id = steamId });

                if (row != null)
                {
                    prefs.Feed = row.Feed != 0;
                    if (Enum.IsDefined(typeof(PanelMode), (byte)row.Mode)) prefs.Mode = (PanelMode)row.Mode;
                    if (Enum.IsDefined(typeof(PanelPlacement), (byte)row.Placement)) prefs.Placement = (PanelPlacement)row.Placement;
                    if (Enum.IsDefined(typeof(PanelSize), (byte)row.Size)) prefs.Size = (PanelSize)row.Size;
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        _prefs[steamId] = prefs;
        return prefs;
    }

    /// <summary>Applies a change at once and saves it in the background.</summary>
    public HudPrefs Update(ulong steamId, Action<HudPrefs> change)
    {
        var prefs = Get(steamId).Clone();
        change(prefs);
        _prefs[steamId] = prefs;

        if (!_missingTable)
        {
            var copy = prefs.Clone();
            Task.Run(() => SaveAsync(steamId, copy));
        }

        return prefs;
    }

    private async Task SaveAsync(ulong steamId, HudPrefs prefs)
    {
        try
        {
            await using var connection = new MySqlConnection(_ranks.DbConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                $@"INSERT INTO `{Table}` (`steamid64`, `feed`, `panel_mode`, `placement`, `size`, `updated_at`)
                   VALUES (@id, @feed, @mode, @placement, @size, UNIX_TIMESTAMP())
                   ON DUPLICATE KEY UPDATE `feed` = VALUES(`feed`), `panel_mode` = VALUES(`panel_mode`),
                       `placement` = VALUES(`placement`), `size` = VALUES(`size`), `updated_at` = VALUES(`updated_at`)",
                new
                {
                    id = steamId,
                    feed = prefs.Feed ? 1 : 0,
                    mode = (byte)prefs.Mode,
                    placement = (byte)prefs.Placement,
                    size = (byte)prefs.Size,
                });
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private sealed class PrefsRow
    {
        public long Feed { get; set; }
        public long Mode { get; set; }
        public long Placement { get; set; }
        public long Size { get; set; }
    }

    private void Fail(Exception e)
    {
        if (e is MySqlException { Number: NoSuchTable })
        {
            if (!_missingTable)
                _ranks.Logger.LogWarning(
                    "HUD settings table `{table}` does not exist - every player gets the defaults and nothing is saved " +
                    "until ranks-hud-settings.sql is run", Table);
            _missingTable = true;
            return;
        }

        _ranks.Logger.LogError("HUD settings: {error}", e.Message);
    }
}
