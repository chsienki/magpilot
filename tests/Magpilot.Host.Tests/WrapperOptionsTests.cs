using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class WrapperOptionsTests
{
    [Fact]
    public void Parse_no_tui_changes_sets_option_and_strips_flag()
    {
        var options = WrapperOptions.Parse(["--magpilot-no-tui-changes", "--resume=abc"]);

        Assert.True(options.NoTuiChanges);
        Assert.Equal(LauncherTuiOptions.None, options.TuiOptions);
        Assert.Equal(["--resume=abc"], options.ForwardArgs);
    }

    [Fact]
    public void Parse_tui_options_combines_named_features()
    {
        var options = WrapperOptions.Parse(
            ["--magpilot-tui-options=term,background,banner", "--resume=abc"]);

        Assert.Equal(
            LauncherTuiOptions.Term | LauncherTuiOptions.Background | LauncherTuiOptions.Banner,
            options.TuiOptions);
        Assert.Equal(["--resume=abc"], options.ForwardArgs);
    }

    [Fact]
    public void Parse_rewrite_alias_enables_each_rewrite_stage()
    {
        var options = WrapperOptions.Parse(["--magpilot-tui-options=rewrite"]);

        Assert.True(options.TuiOptions.Includes(LauncherTuiOptions.Thinking));
        Assert.True(options.TuiOptions.Includes(LauncherTuiOptions.InputBand));
        Assert.True(options.TuiOptions.Includes(LauncherTuiOptions.LegacyColors));
        Assert.False(options.TuiOptions.Includes(LauncherTuiOptions.Palette));
    }

    [Theory]
    [InlineData("--magpilot-tui-options=none,term")]
    [InlineData("--magpilot-tui-options=unknown")]
    public void Parse_tui_options_rejects_invalid_values(string arg)
    {
        var ex = Assert.Throws<ArgumentException>(() => WrapperOptions.Parse([arg]));

        Assert.Contains("--magpilot-tui-options", ex.Message);
    }

    [Fact]
    public void Parse_rejects_no_tui_alias_with_explicit_options()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            WrapperOptions.Parse(
                ["--magpilot-no-tui-changes", "--magpilot-tui-options=background"]));

        Assert.Contains("Contradictory flags", ex.Message);
    }
}
