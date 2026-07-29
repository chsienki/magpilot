using System.Text;
using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class BannerTagInjectorTests
{
    private const string Tag = " (Magpilot v0.1.13)";

    private static string Inject(BannerTagInjector inj, params string[] chunks)
    {
        var sb = new StringBuilder();
        foreach (var c in chunks)
            sb.Append(Encoding.UTF8.GetString(inj.Transform(Encoding.UTF8.GetBytes(c))));
        return sb.ToString();
    }

    private static string Inject(params string[] chunks)
        => Inject(new BannerTagInjector(Tag), chunks);

    [Fact]
    public void Appends_tag_after_banner_anchor()
        => Assert.Equal($"Copilot v1.0.76-3 uses AI.{Tag} ", Inject("Copilot v1.0.76-3 uses AI. "));

    [Fact]
    public void Passthrough_when_anchor_absent()
        => Assert.Equal("no banner in this text", Inject("no banner in this text"));

    [Fact]
    public void Handles_anchor_split_across_chunks()
        => Assert.Equal($"...uses AI.{Tag} rest", Inject("...uses A", "I. rest"));

    [Fact]
    public void Injects_only_once()
        => Assert.Equal($"uses AI.{Tag} then uses AI. again", Inject("uses AI. then uses AI. again"));

    [Fact]
    public void Restarts_match_after_repeated_prefix_byte()
        => Assert.Equal($"uuses AI.{Tag}", Inject("uuses AI."));

    [Fact]
    public void Injects_within_the_real_banner_ansi_run()
    {
        // Grey SGR, copilot's banner sentence, then the logo's colour change.
        var input = "\x1b[38;2;134;134;134mCopilot v1.0.76-3 uses AI. \x1b[38;2;188;68;167mX";
        var expected = $"\x1b[38;2;134;134;134mCopilot v1.0.76-3 uses AI.{Tag} \x1b[38;2;188;68;167mX";
        Assert.Equal(expected, Inject(input));
    }
}
