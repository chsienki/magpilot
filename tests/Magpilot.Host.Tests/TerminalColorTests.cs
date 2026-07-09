using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class TerminalColorTests
{
    [Theory]
    // The common xterm 16-bit-per-channel reply, ST-terminated.
    [InlineData("\x1b]11;rgb:1e1e/1e1e/1e1e\x1b\\", 0x1e, 0x1e, 0x1e)]
    // BEL-terminated variant.
    [InlineData("\x1b]11;rgb:2828/2c2c/3434\a", 0x28, 0x2c, 0x34)]
    // 8-bit-per-channel reply.
    [InlineData("\x1b]11;rgb:1e/1e/1e\x1b\\", 0x1e, 0x1e, 0x1e)]
    // Bare payload with no OSC introducer.
    [InlineData("rgb:ffff/ffff/ffff", 0xff, 0xff, 0xff)]
    // Hash form.
    [InlineData("\x1b]11;#1e1e1e\x1b\\", 0x1e, 0x1e, 0x1e)]
    // 12-hex-digit hash.
    [InlineData("#1e1e1e1e1e1e", 0x1e, 0x1e, 0x1e)]
    public void TryParseOscColorReply_parses_known_forms(string reply, int r, int g, int b)
    {
        Assert.True(TerminalColor.TryParseOscColorReply(reply, out var rgb));
        Assert.Equal((byte)r, rgb.R);
        Assert.Equal((byte)g, rgb.G);
        Assert.Equal((byte)b, rgb.B);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("\x1b]11;rgb:zz/zz/zz\x1b\\")]
    [InlineData("\x1b]11;rgb:1e1e/1e1e\x1b\\")] // only two channels
    public void TryParseOscColorReply_rejects_bad_input(string reply)
    {
        Assert.False(TerminalColor.TryParseOscColorReply(reply, out _));
    }

    [Fact]
    public void RelativeLuminance_is_monotonic_black_to_white()
    {
        var black = TerminalColor.RelativeLuminance(new Rgb(0, 0, 0));
        var grey = TerminalColor.RelativeLuminance(new Rgb(128, 128, 128));
        var white = TerminalColor.RelativeLuminance(new Rgb(255, 255, 255));
        Assert.True(black < grey);
        Assert.True(grey < white);
        Assert.Equal(0.0, black, 3);
        Assert.Equal(1.0, white, 3);
    }

    [Theory]
    [InlineData(0x1e, 0x1e, 0x1e, true)]   // VS Code dark bg
    [InlineData(0x00, 0x00, 0x00, true)]   // black
    [InlineData(0xff, 0xff, 0xff, false)]  // white
    [InlineData(0xf5, 0xf5, 0xf5, false)]  // near-white light theme
    public void IsDark_classifies_backgrounds(int r, int g, int b, bool expectedDark)
    {
        Assert.Equal(expectedDark, TerminalColor.IsDark(new Rgb((byte)r, (byte)g, (byte)b)));
    }

    [Fact]
    public void ColorFgBg_puts_the_load_bearing_bg_index_last()
    {
        Assert.Equal("15;0", TerminalColor.ColorFgBg(isDark: true));
        Assert.Equal("0;15", TerminalColor.ColorFgBg(isDark: false));
    }

    [Fact]
    public void SetPaletteColor_emits_osc4_with_doubled_hex()
    {
        var seq = TerminalColor.SetPaletteColor(4, new Rgb(0x3b, 0x8e, 0xea));
        Assert.Equal("\x1b]4;4;rgb:3b3b/8e8e/eaea\x1b\\", seq);
    }

    [Fact]
    public void ResetColors_resets_palette_fg_and_bg()
    {
        Assert.Equal("\x1b]104\x1b\\\x1b]110\x1b\\\x1b]111\x1b\\", TerminalColor.ResetColors());
    }

    [Fact]
    public void BuildApplySequence_is_empty_when_nothing_overridden()
    {
        Assert.Equal("", TerminalColor.BuildApplySequence(new Dictionary<int, Rgb>(), null, null));
    }

    [Fact]
    public void BuildApplySequence_includes_palette_then_fg_then_bg()
    {
        var palette = new Dictionary<int, Rgb> { [1] = new Rgb(0xff, 0, 0) };
        var seq = TerminalColor.BuildApplySequence(palette, new Rgb(0xd4, 0xd4, 0xd4), new Rgb(0x1e, 0x1e, 0x1e));
        Assert.Contains("\x1b]4;1;rgb:ffff/0000/0000\x1b\\", seq);
        Assert.Contains("\x1b]10;rgb:d4d4/d4d4/d4d4\x1b\\", seq);
        Assert.Contains("\x1b]11;rgb:1e1e/1e1e/1e1e\x1b\\", seq);
        // fg OSC 10 comes before bg OSC 11.
        Assert.True(seq.IndexOf("]10;", StringComparison.Ordinal) < seq.IndexOf("]11;", StringComparison.Ordinal));
    }
}
