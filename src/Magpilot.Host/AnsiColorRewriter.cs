using System.Text;

namespace Magpilot.Host;

/// <summary>
/// Rewrites specific colours in copilot's output as it streams past, so the
/// launcher can fix things the terminal palette can't reach. Two rewrites,
/// both optional:
///
/// <list type="bullet">
///   <item><b>Faint -> thinking colour.</b> copilot renders reasoning text
///   with the terminal's faint attribute (SGR 2), drawn as ~half the
///   foreground brightness and often unreadable on dark backgrounds. When a
///   thinking colour is configured, SGR <c>2</c> (faint on) becomes
///   <c>38;2;R;G;B</c> (set that colour at full intensity) and SGR <c>22</c>
///   (faint off) becomes <c>22;39</c> (also restore the default fg).</item>
///   <item><b>Input-band remap.</b> copilot's composer surface is its
///   <c>backgroundSecondary</c>, a fixed grey (<c>#202020</c>) from copilot's
///   own ramp that isn't derived from the terminal palette. It shows up both
///   as a background (the fill) and as a foreground (the ▀/▄ half-block box
///   edges). When an input-band colour is configured, both forms of
///   <c>#202020</c> are retargeted to it. Only <c>#202020</c> is matched, so
///   diff / selection / link colours are left alone.</item>
/// </list>
///
/// <para>The filter is incremental: an escape split across read buffers is
/// held and resumed on the next call, so arbitrary chunk boundaries are
/// safe.</para>
/// </summary>
internal sealed class AnsiColorRewriter
{
    private const byte Esc = 0x1b;

    // copilot's base-16 backgroundSecondary (the composer band), emitted as a
    // truecolor background set. Matching this exact value keeps the remap
    // off diff / selection / link backgrounds, which use other colours.
    private static readonly string[] BandFrom = ["32", "32", "32"]; // #202020

    private enum State { Normal, AfterEsc, InCsi }

    private readonly string? _setForeground;  // "38;2;R;G;B" for thinking, or null
    private readonly string[]? _bandTo;       // input-band target [r,g,b], or null
    private readonly List<byte> _seq = new(16);
    private State _state = State.Normal;

    public AnsiColorRewriter(Rgb? thinking, Rgb? inputBand)
    {
        _setForeground = thinking is { } t ? $"38;2;{t.R};{t.G};{t.B}" : null;
        _bandTo = inputBand is { } b ? [b.R.ToString(), b.G.ToString(), b.B.ToString()] : null;
    }

    /// <summary>True if at least one rewrite is configured (otherwise the
    /// caller should skip constructing a rewriter at all).</summary>
    public bool IsActive => _setForeground is not null || _bandTo is not null;

    /// <summary>Transform one chunk of copilot output. Partial escape
    /// sequences are carried over to the next call.</summary>
    public byte[] Transform(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length + 16);
        foreach (var b in input)
            Step(b, output);
        return output.ToArray();
    }

    private void Step(byte b, List<byte> output)
    {
        switch (_state)
        {
            case State.Normal:
                if (b == Esc) { _seq.Clear(); _seq.Add(b); _state = State.AfterEsc; }
                else output.Add(b);
                break;

            case State.AfterEsc:
                _seq.Add(b);
                if (b == (byte)'[')
                {
                    _state = State.InCsi;
                }
                else
                {
                    // Not a CSI (OSC, charset select, bare ESC, ...) -- pass
                    // through untouched; we only care about SGR.
                    output.AddRange(_seq);
                    _seq.Clear();
                    _state = State.Normal;
                }
                break;

            case State.InCsi:
                _seq.Add(b);
                // CSI ends at a final byte in 0x40-0x7E (@ .. ~).
                if (b is >= 0x40 and <= 0x7e)
                {
                    EmitCsi(output);
                    _seq.Clear();
                    _state = State.Normal;
                }
                break;
        }
    }

    private void EmitCsi(List<byte> output)
    {
        // _seq == ESC '[' <params> <final>. Only SGR ('m') is rewritten.
        if (_seq[^1] != (byte)'m')
        {
            output.AddRange(_seq);
            return;
        }

        var paramText = Encoding.ASCII.GetString(_seq.ToArray(), 2, _seq.Count - 3);
        var tokens = paramText.Split(';');
        var rebuilt = RewriteSgrParams(tokens);

        output.Add(Esc);
        output.Add((byte)'[');
        output.AddRange(Encoding.ASCII.GetBytes(string.Join(';', rebuilt)));
        output.Add((byte)'m');
    }

    private List<string> RewriteSgrParams(string[] tokens)
    {
        var result = new List<string>(tokens.Length + 4);
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];

            // Extended colour selector: 38/48 ; (2 ; r ; g ; b) | (5 ; n).
            // Copy it verbatim (so an inner "2"/"5" is never read as faint),
            // except retarget copilot's surface grey (#202020) when an
            // input-band colour is configured. It appears as a background
            // (the composer fill) AND as a foreground (the ▀/▄ half-block
            // box edges), so both the 48 and 38 forms are remapped; #202020
            // is far too dark to be real text, so it's only ever decoration.
            if (t is "38" or "48")
            {
                result.Add(t);
                if (i + 1 < tokens.Length)
                {
                    var mode = tokens[i + 1];
                    result.Add(mode);
                    var extra = mode == "2" ? 3 : mode == "5" ? 1 : 0;
                    var sub = new string[Math.Min(extra, tokens.Length - (i + 2))];
                    for (var k = 0; k < sub.Length; k++)
                        sub[k] = tokens[i + 2 + k];

                    if (mode == "2" && _bandTo is not null && sub.Length == 3 &&
                        sub[0] == BandFrom[0] && sub[1] == BandFrom[1] && sub[2] == BandFrom[2])
                        result.AddRange(_bandTo);
                    else
                        result.AddRange(sub);

                    i += 1 + extra;
                }
                continue;
            }

            switch (t)
            {
                case "2" when _setForeground is not null:  // faint on -> thinking colour
                    result.Add(_setForeground);
                    break;
                case "22" when _setForeground is not null: // faint off -> also restore default fg
                    result.Add("22");
                    result.Add("39");
                    break;
                default:
                    result.Add(t);
                    break;
            }
        }
        return result;
    }
}
