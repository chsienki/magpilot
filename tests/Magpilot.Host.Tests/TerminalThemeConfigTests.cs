using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class TerminalThemeConfigTests
{
    [Fact]
    public void ParseThemeJson_reads_palette_foreground_background_thinking_and_band()
    {
        const string json = """
            {
              "palette": { "0": "#1e1e1e", "4": "3b8eea", "15": "#ffffff" },
              "foreground": "#d4d4d4",
              "background": "1e1e1e",
              "thinking": "#7a8a8a",
              "inputBand": "#1a3038"
            }
            """;

        var (palette, fg, bg, thinking, inputBand) = TerminalThemeConfig.ParseThemeJson(json);

        Assert.Equal(3, palette.Count);
        Assert.Equal(new Rgb(0x1e, 0x1e, 0x1e), palette[0]);
        Assert.Equal(new Rgb(0x3b, 0x8e, 0xea), palette[4]);   // bare hex accepted
        Assert.Equal(new Rgb(0xff, 0xff, 0xff), palette[15]);
        Assert.Equal(new Rgb(0xd4, 0xd4, 0xd4), fg);
        Assert.Equal(new Rgb(0x1e, 0x1e, 0x1e), bg);           // bare hex accepted
        Assert.Equal(new Rgb(0x7a, 0x8a, 0x8a), thinking);
        Assert.Equal(new Rgb(0x1a, 0x30, 0x38), inputBand);
    }

    [Fact]
    public void ParseThemeJson_tolerates_missing_sections()
    {
        var (palette, fg, bg, thinking, inputBand) = TerminalThemeConfig.ParseThemeJson("""{ "palette": {} }""");
        Assert.Empty(palette);
        Assert.Null(fg);
        Assert.Null(bg);
        Assert.Null(thinking);
        Assert.Null(inputBand);
    }

    [Fact]
    public void ParseThemeJson_skips_invalid_entries_but_keeps_valid_ones()
    {
        const string json = """
            {
              "palette": { "0": "#1e1e1e", "1": "not-a-colour", "999": "#000000", "2": "#abcdef" }
            }
            """;

        var (palette, _, _, _, _) = TerminalThemeConfig.ParseThemeJson(json);

        Assert.Equal(2, palette.Count);       // index 1 (bad hex) and 999 (out of range) dropped
        Assert.True(palette.ContainsKey(0));
        Assert.True(palette.ContainsKey(2));
        Assert.False(palette.ContainsKey(1));
        Assert.False(palette.ContainsKey(999));
    }

    [Fact]
    public void Default_config_probes_and_enables_github_theme()
    {
        Assert.Equal(BackgroundMode.Auto, TerminalThemeConfig.Default.Background);
        Assert.True(TerminalThemeConfig.Default.EnableGithubTheme);
        Assert.False(TerminalThemeConfig.Default.HasPaletteOverrides);
    }
}
