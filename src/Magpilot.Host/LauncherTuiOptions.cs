namespace Magpilot.Host;

[Flags]
public enum LauncherTuiOptions
{
    None = 0,
    Term = 1 << 0,
    TrueColor = 1 << 1,
    Background = 1 << 2,
    GithubTheme = 1 << 3,
    Palette = 1 << 4,
    Thinking = 1 << 5,
    InputBand = 1 << 6,
    LegacyColors = 1 << 7,
    Rewrite = Thinking | InputBand | LegacyColors,
    Banner = 1 << 8,
    All = Term | TrueColor | Background | GithubTheme | Palette | Rewrite | Banner,
}

internal static class LauncherTuiOptionSet
{
    private static readonly (string Name, LauncherTuiOptions Value)[] s_namedOptions =
    [
        ("term", LauncherTuiOptions.Term),
        ("truecolor", LauncherTuiOptions.TrueColor),
        ("background", LauncherTuiOptions.Background),
        ("github-theme", LauncherTuiOptions.GithubTheme),
        ("palette", LauncherTuiOptions.Palette),
        ("thinking", LauncherTuiOptions.Thinking),
        ("input-band", LauncherTuiOptions.InputBand),
        ("legacy-colors", LauncherTuiOptions.LegacyColors),
        ("banner", LauncherTuiOptions.Banner),
    ];

    public static bool Includes(this LauncherTuiOptions options, LauncherTuiOptions feature) =>
        (options & feature) == feature;

    public static LauncherTuiOptions Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("--magpilot-tui-options requires a comma-separated value.");

        var tokens = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            throw new ArgumentException("--magpilot-tui-options requires a comma-separated value.");

        if (tokens.Any(token => token.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            if (tokens.Length != 1)
                throw new ArgumentException("'all' must be the only --magpilot-tui-options value.");
            return LauncherTuiOptions.All;
        }

        if (tokens.Any(token => token.Equals("none", StringComparison.OrdinalIgnoreCase)))
        {
            if (tokens.Length != 1)
                throw new ArgumentException("'none' must be the only --magpilot-tui-options value.");
            return LauncherTuiOptions.None;
        }

        var result = LauncherTuiOptions.None;
        foreach (var token in tokens)
        {
            if (token.Equals("rewrite", StringComparison.OrdinalIgnoreCase))
            {
                result |= LauncherTuiOptions.Rewrite;
                continue;
            }

            var match = s_namedOptions.FirstOrDefault(
                option => option.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (match.Value == LauncherTuiOptions.None)
            {
                var valid = string.Join(", ", s_namedOptions.Select(option => option.Name));
                throw new ArgumentException(
                    $"Unknown --magpilot-tui-options value '{token}'. Valid values: all, none, rewrite, {valid}.");
            }

            result |= match.Value;
        }

        return result;
    }

    public static string[] Names(LauncherTuiOptions options)
    {
        if (options == LauncherTuiOptions.None)
            return ["none"];
        if (options == LauncherTuiOptions.All)
            return ["all"];

        return s_namedOptions
            .Where(option => options.Includes(option.Value))
            .Select(option => option.Name)
            .ToArray();
    }
}
