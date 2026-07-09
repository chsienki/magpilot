using System.Text.Json;

namespace Magpilot.Host;

/// <summary>How the local client decides copilot's background for theming.</summary>
internal enum BackgroundMode
{
    /// <summary>Probe the real terminal via OSC 11 (the default).</summary>
    Auto,
    Dark,
    Light,
}

/// <summary>
/// The local client's terminal-theming configuration. copilot renders its
/// own TUI, so magpilot can only influence it two ways: environment hints
/// it passes to the child, and OSC sequences it injects into the real
/// terminal around copilot's output. This record captures both surfaces.
///
/// <para>Loaded from <see cref="InstallConfig"/> (process env first, then
/// the installed <c>magpilot.env</c>), plus an optional per-theme JSON file
/// for palette overrides.</para>
/// </summary>
internal sealed record TerminalThemeConfig(
    BackgroundMode Background,
    bool EnableGithubTheme,
    IReadOnlyDictionary<int, Rgb> Palette,
    Rgb? Foreground,
    Rgb? BackgroundColor)
{
    public static TerminalThemeConfig Default { get; } =
        new(BackgroundMode.Auto, EnableGithubTheme: true,
            new Dictionary<int, Rgb>(), Foreground: null, BackgroundColor: null);

    public bool HasPaletteOverrides =>
        Palette.Count > 0 || Foreground is not null || BackgroundColor is not null;

    public static TerminalThemeConfig Load()
    {
        var background = InstallConfig.ResolveValue("MAGPILOT_TERM_BACKGROUND")?.Trim().ToLowerInvariant() switch
        {
            "dark" => BackgroundMode.Dark,
            "light" => BackgroundMode.Light,
            _ => BackgroundMode.Auto,
        };

        var enableGithub = InstallConfig.ResolveValue("MAGPILOT_TERM_ENABLE_GITHUB_THEME")?.Trim() switch
        {
            "0" or "false" or "no" => false,
            _ => true,
        };

        var (palette, fg, bg) = LoadPalette();
        return new TerminalThemeConfig(background, enableGithub, palette, fg, bg);
    }

    // A theme file supplies palette / fg / bg overrides. Resolution:
    //   MAGPILOT_TERM_THEME_FILE  -> an explicit path (handy in dev)
    //   MAGPILOT_TERM_THEME=<name> -> <install>/config/themes/<name>.json
    // Absent or unreadable => no overrides (copilot's own theme stands).
    private static (IReadOnlyDictionary<int, Rgb>, Rgb?, Rgb?) LoadPalette()
    {
        var empty = ((IReadOnlyDictionary<int, Rgb>)new Dictionary<int, Rgb>(), (Rgb?)null, (Rgb?)null);

        var path = InstallConfig.ResolveValue("MAGPILOT_TERM_THEME_FILE");
        if (string.IsNullOrEmpty(path))
        {
            var name = InstallConfig.ResolveValue("MAGPILOT_TERM_THEME");
            if (string.IsNullOrEmpty(name)) return empty;
            var dir = ThemesDir();
            if (dir is null) return empty;
            path = Path.Combine(dir, $"{name}.json");
        }

        if (!File.Exists(path)) return empty;
        try
        {
            return ParseThemeJson(File.ReadAllText(path));
        }
        catch
        {
            // A malformed theme file must not stop the user reaching copilot.
            return empty;
        }
    }

    /// <summary>Parse a theme JSON document into palette + fg/bg overrides.
    /// Split out from file I/O so it can be unit-tested directly.</summary>
    public static (IReadOnlyDictionary<int, Rgb>, Rgb?, Rgb?) ParseThemeJson(string json)
    {
        var palette = new Dictionary<int, Rgb>();
        Rgb? fg = null, bg = null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("palette", out var pal) && pal.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in pal.EnumerateObject())
            {
                if (int.TryParse(entry.Name, out var index) && index is >= 0 and <= 255 &&
                    entry.Value.ValueKind == JsonValueKind.String &&
                    TryParseColor(entry.Value.GetString(), out var colour))
                {
                    palette[index] = colour;
                }
            }
        }

        if (root.TryGetProperty("foreground", out var f) && f.ValueKind == JsonValueKind.String &&
            TryParseColor(f.GetString(), out var fgColour))
            fg = fgColour;

        if (root.TryGetProperty("background", out var b) && b.ValueKind == JsonValueKind.String &&
            TryParseColor(b.GetString(), out var bgColour))
            bg = bgColour;

        return (palette, fg, bg);
    }

    // Accept "#rrggbb" or bare "rrggbb".
    private static bool TryParseColor(string? value, out Rgb rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s[0] != '#') s = "#" + s;
        return TerminalColor.TryParseHash(s, out rgb);
    }

    private static string? ThemesDir()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return null;
        var binDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(binDir)) return null;
        var installDir = Path.GetDirectoryName(binDir);
        if (string.IsNullOrEmpty(installDir)) return null;
        return Path.Combine(installDir, "config", "themes");
    }
}
