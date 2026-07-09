using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class TerminalThemingTests
{
    [Fact]
    public void PinnedBackground_maps_config_to_dark_light_or_null()
    {
        Assert.True(TerminalTheming.PinnedBackground(WithBackground(BackgroundMode.Dark)));
        Assert.False(TerminalTheming.PinnedBackground(WithBackground(BackgroundMode.Light)));
        Assert.Null(TerminalTheming.PinnedBackground(WithBackground(BackgroundMode.Auto)));
    }

    [Fact]
    public void PopulateChildEnv_sets_colorfgbg_only_when_background_known()
    {
        var env = new Dictionary<string, string>();
        TerminalTheming.PopulateChildEnv(env, TerminalThemeConfig.Default, isDark: true);
        Assert.Equal("15;0", env["COLORFGBG"]);

        var env2 = new Dictionary<string, string>();
        TerminalTheming.PopulateChildEnv(env2, TerminalThemeConfig.Default, isDark: null);
        Assert.False(env2.ContainsKey("COLORFGBG"));
    }

    [Fact]
    public void PopulateChildEnv_sets_github_theme_flag_when_enabled()
    {
        var enabled = new Dictionary<string, string>();
        TerminalTheming.PopulateChildEnv(enabled, TerminalThemeConfig.Default, isDark: null);
        Assert.Equal("1", enabled["COPILOT_GITHUB_THEME"]);

        var disabled = new Dictionary<string, string>();
        TerminalTheming.PopulateChildEnv(disabled, DefaultWith(enableGithub: false), isDark: null);
        Assert.False(disabled.ContainsKey("COPILOT_GITHUB_THEME"));
    }

    private static TerminalThemeConfig WithBackground(BackgroundMode mode) =>
        TerminalThemeConfig.Default with { Background = mode };

    private static TerminalThemeConfig DefaultWith(bool enableGithub) =>
        TerminalThemeConfig.Default with { EnableGithubTheme = enableGithub };
}
