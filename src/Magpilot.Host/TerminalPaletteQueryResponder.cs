using System.Text;

namespace Magpilot.Host;

internal readonly record struct TerminalQueryTransform(byte[] Output, byte[] Reply);

/// <summary>
/// Answers Copilot's terminal palette queries inside the PTY so it observes
/// the configured theme deterministically instead of racing the outer
/// terminal's processing of the matching OSC overrides. Matching queries are
/// removed from visible output and their replies are written back to Copilot's
/// input stream.
/// </summary>
internal sealed class TerminalPaletteQueryResponder(
    IReadOnlyDictionary<int, Rgb> palette,
    Rgb? foreground,
    Rgb? background)
{
    private const byte Esc = 0x1b;
    private const byte Bel = 0x07;

    private enum State
    {
        Normal,
        AfterEsc,
        InOsc,
        InOscAfterEsc,
    }

    private readonly List<byte> _sequence = new(64);
    private State _state;

    public TerminalQueryTransform Transform(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length);
        var replies = new List<byte>();

        foreach (var value in input)
        {
            Step(value, output, replies);
        }

        return new TerminalQueryTransform(output.ToArray(), replies.ToArray());
    }

    private void Step(byte value, List<byte> output, List<byte> replies)
    {
        switch (_state)
        {
            case State.Normal:
                if (value == Esc)
                {
                    _sequence.Clear();
                    _sequence.Add(value);
                    _state = State.AfterEsc;
                }
                else
                {
                    output.Add(value);
                }
                break;

            case State.AfterEsc:
                _sequence.Add(value);
                if (value == (byte)']')
                {
                    _state = State.InOsc;
                }
                else
                {
                    FlushSequence(output);
                }
                break;

            case State.InOsc:
                _sequence.Add(value);
                if (value == Bel)
                {
                    CompleteOsc(output, replies, terminatorLength: 1);
                }
                else if (value == Esc)
                {
                    _state = State.InOscAfterEsc;
                }
                break;

            case State.InOscAfterEsc:
                _sequence.Add(value);
                if (value == (byte)'\\')
                {
                    CompleteOsc(output, replies, terminatorLength: 2);
                }
                else if (value != Esc)
                {
                    _state = State.InOsc;
                }
                break;
        }
    }

    private void CompleteOsc(List<byte> output, List<byte> replies, int terminatorLength)
    {
        var bodyLength = _sequence.Count - 2 - terminatorLength;
        var body = Encoding.ASCII.GetString(_sequence.ToArray(), 2, bodyLength);
        var reply = ReplyFor(body);

        if (reply is null)
        {
            output.AddRange(_sequence);
        }
        else
        {
            replies.AddRange(Encoding.ASCII.GetBytes(reply));
        }

        _sequence.Clear();
        _state = State.Normal;
    }

    private string? ReplyFor(string body)
    {
        if (body == "10;?" && foreground is { } foregroundColor)
        {
            return TerminalColor.SetForeground(foregroundColor);
        }

        if (body == "11;?" && background is { } backgroundColor)
        {
            return TerminalColor.SetBackground(backgroundColor);
        }

        if (!body.StartsWith("4;", StringComparison.Ordinal) ||
            !body.EndsWith(";?", StringComparison.Ordinal) ||
            !int.TryParse(body.AsSpan(2, body.Length - 4), out var index) ||
            !palette.TryGetValue(index, out var color))
        {
            return null;
        }

        return TerminalColor.SetPaletteColor(index, color);
    }

    private void FlushSequence(List<byte> output)
    {
        output.AddRange(_sequence);
        _sequence.Clear();
        _state = State.Normal;
    }
}
