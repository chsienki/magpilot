using System.Text;
using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class TerminalPaletteQueryResponderTests
{
    [Fact]
    public void Answers_palette_foreground_and_background_queries()
    {
        var responder = CreateResponder();
        var result = Transform(
            responder,
            "before",
            "\x1b]4;14;?\x1b\\",
            "\x1b]10;?\a",
            "\x1b]11;?\x1b\\",
            "after");

        Assert.Equal("beforeafter", result.Output);
        Assert.Equal(
            TerminalColor.SetPaletteColor(14, new Rgb(0x68, 0xea, 0xd9)) +
            TerminalColor.SetForeground(new Rgb(0xc2, 0xcc, 0xc6)) +
            TerminalColor.SetBackground(new Rgb(0x00, 0x2b, 0x36)),
            result.Reply);
    }

    [Fact]
    public void Query_split_across_chunks_is_reassembled()
    {
        var responder = CreateResponder();
        var result = Transform(responder, "\x1b]", "4;1;", "?\x1b", "\\");

        Assert.Equal("", result.Output);
        Assert.Equal(
            TerminalColor.SetPaletteColor(1, new Rgb(0xf0, 0x5b, 0x52)),
            result.Reply);
    }

    [Fact]
    public void Unrelated_or_unsupported_osc_sequences_are_untouched()
    {
        var responder = CreateResponder();
        var input = "\x1b]8;id=x;https://example.test\x1b\\link\x1b]8;;\x1b\\" +
            "\x1b]4;200;?\x1b\\";

        var result = Transform(responder, input);

        Assert.Equal(input, result.Output);
        Assert.Equal("", result.Reply);
    }

    [Fact]
    public void Queries_without_a_configured_override_are_left_for_the_outer_terminal()
    {
        var responder = new TerminalPaletteQueryResponder(
            new Dictionary<int, Rgb>(),
            foreground: null,
            background: null);
        var input = "\x1b]4;3;?\x1b\\\x1b]10;?\x1b\\\x1b]11;?\x1b\\";

        var result = Transform(responder, input);

        Assert.Equal(input, result.Output);
        Assert.Equal("", result.Reply);
    }

    private static TerminalPaletteQueryResponder CreateResponder() =>
        new(
            new Dictionary<int, Rgb>
            {
                [1] = new(0xf0, 0x5b, 0x52),
                [14] = new(0x68, 0xea, 0xd9),
            },
            new Rgb(0xc2, 0xcc, 0xc6),
            new Rgb(0x00, 0x2b, 0x36));

    private static (string Output, string Reply) Transform(
        TerminalPaletteQueryResponder responder,
        params string[] chunks)
    {
        var output = new StringBuilder();
        var reply = new StringBuilder();

        foreach (var chunk in chunks)
        {
            var transformed = responder.Transform(Encoding.ASCII.GetBytes(chunk));
            output.Append(Encoding.ASCII.GetString(transformed.Output));
            reply.Append(Encoding.ASCII.GetString(transformed.Reply));
        }

        return (output.ToString(), reply.ToString());
    }
}
