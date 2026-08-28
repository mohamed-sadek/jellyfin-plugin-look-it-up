using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.LookItUp.Configuration;

/// <summary>
/// Survives Jellyfin rewriting <c>Jellyfin.Plugin.LookItUp.xml</c> to constructor defaults
/// when plugin XML fails to deserialize after an update.
/// </summary>
internal static class PopupAppearanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// Writes current popup look settings so they can be restored after a config XML reset.
    /// </summary>
    public static void Save(IApplicationPaths paths, PluginConfiguration config)
    {
        try
        {
            var path = GetPath(paths);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(Snapshot.From(config), JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Appearance backup must never break plugin load or save.
        }
    }

    /// <summary>
    /// Overlays saved appearance onto <paramref name="config"/>.
    /// </summary>
    /// <returns>True when at least one field differed from the sidecar.</returns>
    public static bool TryRestore(IApplicationPaths paths, PluginConfiguration config)
    {
        try
        {
            var path = GetPath(paths);
            if (!File.Exists(path))
            {
                return false;
            }

            var loaded = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), JsonOptions);
            return loaded is not null && loaded.ApplyTo(config);
        }
        catch
        {
            return false;
        }
    }

    private static string GetPath(IApplicationPaths paths)
    {
        var root = paths.PluginConfigurationsPath
                   ?? paths.ProgramDataPath
                   ?? Path.GetTempPath();
        return Path.Combine(root, "LookItUp", "popup-appearance.json");
    }

    private sealed class Snapshot
    {
        public int? PopupDurationMs { get; set; }
        public int? PopupDelayMs { get; set; }
        public int? PopupFontSizePx { get; set; }
        public string? PopupTextColor { get; set; }
        public string? PopupBorderColor { get; set; }
        public string? PopupBackgroundColor { get; set; }
        public int? PopupMaxWidthPx { get; set; }
        public bool? PopupScaleWithScreen { get; set; }
        public string? PopupPlacement { get; set; }
        public int? PopupEdgeOffsetPct { get; set; }

        public static Snapshot From(PluginConfiguration config) => new()
        {
            PopupDurationMs = config.PopupDurationMs,
            PopupDelayMs = config.PopupDelayMs,
            PopupFontSizePx = config.PopupFontSizePx,
            PopupTextColor = config.PopupTextColor,
            PopupBorderColor = config.PopupBorderColor,
            PopupBackgroundColor = config.PopupBackgroundColor,
            PopupMaxWidthPx = config.PopupMaxWidthPx,
            PopupScaleWithScreen = config.PopupScaleWithScreen,
            PopupPlacement = config.PopupPlacement,
            PopupEdgeOffsetPct = config.PopupEdgeOffsetPct
        };

        public bool ApplyTo(PluginConfiguration config)
        {
            var changed = false;
            changed |= ApplyInt(PopupDurationMs, 1000, 30000, config.PopupDurationMs, v => config.PopupDurationMs = v);
            changed |= ApplyInt(PopupDelayMs, 0, 10000, config.PopupDelayMs, v => config.PopupDelayMs = v);
            changed |= ApplyInt(PopupFontSizePx, 10, 48, config.PopupFontSizePx, v => config.PopupFontSizePx = v);
            changed |= ApplyInt(PopupMaxWidthPx, 180, 560, config.PopupMaxWidthPx, v => config.PopupMaxWidthPx = v);
            changed |= ApplyInt(PopupEdgeOffsetPct, 2, 40, config.PopupEdgeOffsetPct, v => config.PopupEdgeOffsetPct = v);
            if (PopupScaleWithScreen is bool scale && config.PopupScaleWithScreen != scale)
            {
                config.PopupScaleWithScreen = scale;
                changed = true;
            }

            changed |= AssignColor(config.PopupTextColor, PopupTextColor, v => config.PopupTextColor = v);
            changed |= AssignColor(config.PopupBorderColor, PopupBorderColor, v => config.PopupBorderColor = v);
            changed |= AssignColor(config.PopupBackgroundColor, PopupBackgroundColor, v => config.PopupBackgroundColor = v);
            if (!string.IsNullOrWhiteSpace(PopupPlacement)
                && !string.Equals(config.PopupPlacement, PopupPlacement.Trim(), StringComparison.Ordinal))
            {
                config.PopupPlacement = PopupPlacement.Trim();
                changed = true;
            }

            return changed;
        }

        private static bool ApplyInt(int? value, int min, int max, int current, Action<int> set)
        {
            if (value is null)
            {
                return false;
            }

            var next = Math.Clamp(value.Value, min, max);
            if (current == next)
            {
                return false;
            }

            set(next);
            return true;
        }

        private static bool AssignColor(string current, string? value, Action<string> set)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (string.Equals(current, trimmed, StringComparison.Ordinal))
            {
                return false;
            }

            set(trimmed);
            return true;
        }
    }
}
