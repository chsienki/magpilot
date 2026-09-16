using System.Text.Json;

namespace Magpilot.Host;

internal sealed record TerminalPaletteEntry(int Index, string Color);

internal sealed record TerminalLaunchManifest(
    DateTimeOffset CapturedAtUtc,
    string LaunchMode,
    string[] EnabledOptions,
    string ConPtyImplementation,
    string? MsSystem,
    string? Shell,
    string? TermProgram,
    string? WindowsTerminalSession,
    bool InputRedirected,
    bool OutputRedirected,
    int? Columns,
    int? Rows,
    string? Term,
    string? ColorTerm,
    string? ColorFgBg,
    string? CopilotGithubTheme,
    string? BackgroundMode,
    string? DetectedBackground,
    bool? ResolvedIsDark,
    bool PaletteApplied,
    bool RewriteEnabled,
    bool ThinkingRewriteEnabled,
    bool InputBandRewriteEnabled,
    bool LegacyColorRewriteEnabled,
    bool BannerEnabled,
    string? ThemeName,
    string? ThemeFile,
    TerminalPaletteEntry[] Palette,
    string? Foreground,
    string? BackgroundColor,
    string? Thinking,
    string? InputBand,
    bool LegacyDefaultColors,
    int? DumpMilliseconds,
    string? RawDumpPath,
    string? PostDumpPath);

internal static class TerminalDiagnostics
{
    private const string RawDumpVariable = "MAGPILOT_TERM_DUMP";
    private const string PostDumpVariable = "MAGPILOT_TERM_DUMP_POST";
    private const string ManifestVariable = "MAGPILOT_TERM_MANIFEST";
    private const string DumpMillisecondsVariable = "MAGPILOT_TERM_DUMP_MS";

    public static bool CaptureRequested =>
        HasValue(RawDumpVariable) ||
        HasValue(PostDumpVariable) ||
        HasValue(ManifestVariable);

    public static TimeSpan? ResolveDumpDuration()
    {
        var raw = Environment.GetEnvironmentVariable(DumpMillisecondsVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!int.TryParse(raw, out var milliseconds) || milliseconds <= 0)
        {
            throw new ArgumentException(
                $"{DumpMillisecondsVariable} must be a positive integer number of milliseconds.");
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public static void WriteManifest(
        string launchMode,
        LauncherTuiOptions options,
        string conPtyImplementation,
        int? columns,
        int? rows,
        Func<string, string?> childEnvironment,
        TerminalThemeConfig? theme,
        Rgb? detectedBackground,
        bool? resolvedIsDark,
        bool paletteApplied,
        bool thinkingRewriteEnabled,
        bool inputBandRewriteEnabled,
        bool legacyColorRewriteEnabled,
        bool bannerEnabled,
        TimeSpan? dumpDuration)
    {
        var path = Environment.GetEnvironmentVariable(ManifestVariable);
        if (string.IsNullOrWhiteSpace(path))
            return;

        var manifest = new TerminalLaunchManifest(
            DateTimeOffset.UtcNow,
            launchMode,
            LauncherTuiOptionSet.Names(options),
            conPtyImplementation,
            Environment.GetEnvironmentVariable("MSYSTEM"),
            Environment.GetEnvironmentVariable("SHELL"),
            Environment.GetEnvironmentVariable("TERM_PROGRAM"),
            Environment.GetEnvironmentVariable("WT_SESSION"),
            Console.IsInputRedirected,
            Console.IsOutputRedirected,
            columns,
            rows,
            childEnvironment("TERM"),
            childEnvironment("COLORTERM"),
            childEnvironment("COLORFGBG"),
            childEnvironment("COPILOT_GITHUB_THEME"),
            theme?.Background.ToString(),
            detectedBackground is { } background ? FormatColor(background) : null,
            resolvedIsDark,
            paletteApplied,
            thinkingRewriteEnabled || inputBandRewriteEnabled || legacyColorRewriteEnabled,
            thinkingRewriteEnabled,
            inputBandRewriteEnabled,
            legacyColorRewriteEnabled,
            bannerEnabled,
            InstallConfig.ResolveValue("MAGPILOT_TERM_THEME"),
            InstallConfig.ResolveValue("MAGPILOT_TERM_THEME_FILE"),
            theme?.Palette
                .OrderBy(entry => entry.Key)
                .Select(entry => new TerminalPaletteEntry(entry.Key, FormatColor(entry.Value)))
                .ToArray() ?? [],
            theme?.Foreground is { } foreground ? FormatColor(foreground) : null,
            theme?.BackgroundColor is { } backgroundColor ? FormatColor(backgroundColor) : null,
            theme?.Thinking is { } thinking ? FormatColor(thinking) : null,
            theme?.InputBand is { } inputBand ? FormatColor(inputBand) : null,
            theme?.LegacyDefaultColors ?? false,
            dumpDuration is { } duration ? (int)duration.TotalMilliseconds : null,
            Environment.GetEnvironmentVariable(RawDumpVariable),
            Environment.GetEnvironmentVariable(PostDumpVariable));

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(manifest, HostGeneralJsonContext.Default.TerminalLaunchManifest));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: failed to write terminal diagnostics manifest '{path}': {ex.Message}");
        }
    }

    private static bool HasValue(string variable) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable));

    private static string FormatColor(Rgb color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
