using System.Text;
using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class AnsiColorRewriterTests
{
    // #93a1a1 -> "38;2;147;161;161"
    private static readonly Rgb Thinking = new(0x93, 0xa1, 0xa1);
    private const string Set = "38;2;147;161;161";

    // #1a3038 -> "26;48;56"
    private static readonly Rgb Band = new(0x1a, 0x30, 0x38);
    private const string BandTo = "26;48;56";

    private static string Rewrite(AnsiColorRewriter r, params string[] chunks)
    {
        var sb = new StringBuilder();
        foreach (var c in chunks)
            sb.Append(Encoding.ASCII.GetString(r.Transform(Encoding.ASCII.GetBytes(c))));
        return sb.ToString();
    }

    private static string RewriteThinking(params string[] chunks)
        => Rewrite(new AnsiColorRewriter(Thinking, null), chunks);

    private static string RewriteBand(params string[] chunks)
        => Rewrite(new AnsiColorRewriter(null, Band), chunks);

    // --- faint -> thinking colour ---

    [Fact]
    public void Faint_on_becomes_explicit_foreground()
        => Assert.Equal($"\x1b[{Set}m", RewriteThinking("\x1b[2m"));

    [Fact]
    public void Faint_off_also_resets_default_foreground()
        => Assert.Equal("\x1b[22;39m", RewriteThinking("\x1b[22m"));

    [Fact]
    public void Faint_combined_with_italic_keeps_italic()
        => Assert.Equal($"\x1b[3;{Set}m", RewriteThinking("\x1b[3;2m"));

    [Fact]
    public void Truecolor_literal_two_is_not_mistaken_for_faint()
        => Assert.Equal("\x1b[38;2;10;20;30m", RewriteThinking("\x1b[38;2;10;20;30m"));

    [Fact]
    public void Full_dim_segment_recoloured_end_to_end()
        => Assert.Equal($"before \x1b[{Set}mThought\x1b[22;39m after",
            RewriteThinking("before \x1b[2mThought\x1b[22m after"));

    [Fact]
    public void Escape_split_across_chunks_is_reassembled()
        => Assert.Equal($"\x1b[{Set}m", RewriteThinking("\x1b", "[2", "m"));

    // --- input-band remap ---

    [Fact]
    public void Input_band_background_is_retargeted()
        => Assert.Equal($"\x1b[48;2;{BandTo}m", RewriteBand("\x1b[48;2;32;32;32m"));

    [Fact]
    public void Other_backgrounds_are_untouched()
        => Assert.Equal("\x1b[48;2;10;20;30m", RewriteBand("\x1b[48;2;10;20;30m"));

    [Fact]
    public void Foreground_matching_band_source_is_also_remapped()   // the ▀/▄ box edges
        => Assert.Equal($"\x1b[38;2;{BandTo}m", RewriteBand("\x1b[38;2;32;32;32m"));

    [Fact]
    public void Other_foregrounds_are_untouched()
        => Assert.Equal("\x1b[38;2;10;20;30m", RewriteBand("\x1b[38;2;10;20;30m"));

    [Fact]
    public void Band_remap_leaves_faint_alone_when_no_thinking_colour()
        => Assert.Equal("\x1b[2m", RewriteBand("\x1b[2m"));

    // --- both configured ---

    [Fact]
    public void Both_rewrites_apply_together()
    {
        var r = new AnsiColorRewriter(Thinking, Band);
        var result = Rewrite(r, "\x1b[48;2;32;32;32m\x1b[2mhi\x1b[22m");
        Assert.Equal($"\x1b[48;2;{BandTo}m\x1b[{Set}mhi\x1b[22;39m", result);
    }

    // --- untouched general cases ---

    [Fact]
    public void Reset_all_is_untouched()
        => Assert.Equal("\x1b[0m", Rewrite(new AnsiColorRewriter(Thinking, Band), "\x1b[0m"));

    [Fact]
    public void Non_sgr_csi_is_untouched()
        => Assert.Equal("\x1b[10;5H", Rewrite(new AnsiColorRewriter(Thinking, Band), "\x1b[10;5H"));

    [Fact]
    public void Osc_sequence_passes_through()
        => Assert.Equal("\x1b]11;?\x07", Rewrite(new AnsiColorRewriter(Thinking, Band), "\x1b]11;?\x07"));

    [Fact]
    public void IsActive_reflects_configuration()
    {
        Assert.True(new AnsiColorRewriter(Thinking, null).IsActive);
        Assert.True(new AnsiColorRewriter(null, Band).IsActive);
        Assert.False(new AnsiColorRewriter(null, null).IsActive);
    }
}
