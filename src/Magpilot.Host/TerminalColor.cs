using System.Globalization;
using System.Text;

namespace Magpilot.Host;

/// <summary>An 8-bit-per-channel RGB colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// Pure helpers for terminal colour handling: parsing the replies an
/// <c>xterm</c>-style terminal sends to OSC 10/11 colour queries, deciding
/// whether a background is dark or light, and generating the OSC sequences
/// used to (a) tell copilot which background it has via <c>COLORFGBG</c> and
/// (b) remap the terminal's ANSI palette for user colour overrides.
///
/// All members are deterministic and side-effect free so the byte-level
/// parsing (the part most likely to meet a terminal that formats its reply
/// slightly differently) is unit-testable without a real terminal.
/// </summary>
internal static class TerminalColor
{
    private const char Esc = '\x1b';
    private const char Bel = '\a';

    /// <summary>The <c>ESC ] 11 ; ? ST</c> probe that asks the terminal for
    /// its background colour. The reply is parsed by
    /// <see cref="TryParseOscColorReply"/>.</summary>
    public const string BackgroundQuery = "\x1b]11;?\x1b\\";

    /// <summary>
    /// Parse an OSC 10/11 colour reply into an <see cref="Rgb"/>. Accepts the
    /// forms terminals actually emit, e.g.
    /// <c>ESC ]11;rgb:1e1e/1e1e/1e1e ST</c> (16-bit channels, the common
    /// xterm form), <c>rgb:1e/1e/1e</c> (8-bit), and a bare <c>#1e1e1e</c>.
    /// Trailing ST (<c>ESC \</c>) or BEL terminators are tolerated, as is a
    /// surrounding OSC introducer. Returns false if no colour can be found.
    /// </summary>
    public static bool TryParseOscColorReply(string reply, out Rgb rgb)
    {
        rgb = default;
        if (string.IsNullOrEmpty(reply)) return false;

        // Strip an OSC introducer + terminator if present so we can accept
        // either the raw reply or just its colour payload.
        var body = reply.Trim().Trim(Esc, Bel, '\\');

        var rgbIdx = body.IndexOf("rgb:", StringComparison.OrdinalIgnoreCase);
        if (rgbIdx >= 0)
            return TryParseRgbSpec(body[(rgbIdx + 4)..], out rgb);

        var hashIdx = body.IndexOf('#');
        if (hashIdx >= 0)
            return TryParseHash(body[hashIdx..], out rgb);

        return false;
    }

    /// <summary>Parse a bare <c>#rrggbb</c> (or <c>#rgb</c> /
    /// <c>#rrrrggggbbbb</c>) hex colour.</summary>
    public static bool TryParseHash(string hash, out Rgb rgb)
    {
        rgb = default;
        // Take the '#', then the longest run of hex digits after it.
        if (hash.Length == 0 || hash[0] != '#') return false;
        var end = 1;
        while (end < hash.Length && Uri.IsHexDigit(hash[end])) end++;
        var digits = hash[1..end];
        if (digits.Length % 3 != 0 || digits.Length == 0) return false;
        var per = digits.Length / 3;
        return TryScaleChannels(digits[..per], digits[per..(2 * per)], digits[(2 * per)..], out rgb);
    }

    // "RRRR/GGGG/BBBB" or "RR/GG/BB" -> scaled 8-bit RGB.
    private static bool TryParseRgbSpec(string spec, out Rgb rgb)
    {
        rgb = default;
        // Cut off any trailing terminator remnants (e.g. a stray ST/BEL).
        var slashParts = spec.Split('/');
        if (slashParts.Length < 3) return false;
        // The third channel may carry trailing junk after the hex digits.
        var b = slashParts[2];
        var bEnd = 0;
        while (bEnd < b.Length && Uri.IsHexDigit(b[bEnd])) bEnd++;
        return TryScaleChannels(slashParts[0], slashParts[1], b[..bEnd], out rgb);
    }

    private static bool TryScaleChannels(string r, string g, string b, out Rgb rgb)
    {
        rgb = default;
        if (!TryScaleChannel(r, out var rr) ||
            !TryScaleChannel(g, out var gg) ||
            !TryScaleChannel(b, out var bb))
            return false;
        rgb = new Rgb(rr, gg, bb);
        return true;
    }

    // Parse a hex channel of arbitrary width and scale it to 8 bits. A
    // 4-hex-digit channel (0..65535, the xterm reply width) maps to 0..255
    // by taking the high byte; other widths scale proportionally.
    private static bool TryScaleChannel(string hex, out byte value)
    {
        value = 0;
        if (hex.Length is 0 or > 4) return false;
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            return false;
        var max = (1 << (4 * hex.Length)) - 1;
        value = (byte)Math.Round(raw * 255.0 / max);
        return true;
    }

    /// <summary>
    /// Relative luminance (0..1) per the sRGB coefficients, with gamma
    /// linearisation. Used only to classify a background as dark or light,
    /// so the exact model matters less than being monotonic and stable.
    /// </summary>
    public static double RelativeLuminance(Rgb c)
    {
        static double Lin(byte channel)
        {
            var s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>True if the colour reads as a dark background (luminance
    /// below the mid-point).</summary>
    public static bool IsDark(Rgb background) => RelativeLuminance(background) < 0.5;

    /// <summary>
    /// The <c>COLORFGBG</c> value copilot reads as its dark/light fallback
    /// when its OSC 11 query goes unanswered (as it does under ConPTY). The
    /// form is <c>"fg;bg"</c> with ANSI palette indices; only the trailing
    /// background index is load-bearing (0 = dark, 15 = light) per the
    /// long-standing rxvt convention every consumer follows.
    /// </summary>
    public static string ColorFgBg(bool isDark) => isDark ? "15;0" : "0;15";

    /// <summary>Emit <c>OSC 4 ; index ; rgb:... ST</c> to set an ANSI palette
    /// entry (index 0..255).</summary>
    public static string SetPaletteColor(int index, Rgb c) =>
        $"{Esc}]4;{index};{RgbSpec(c)}{Esc}\\";

    /// <summary>Emit <c>OSC 10 ; rgb:... ST</c> to set the default
    /// foreground.</summary>
    public static string SetForeground(Rgb c) => $"{Esc}]10;{RgbSpec(c)}{Esc}\\";

    /// <summary>Emit <c>OSC 11 ; rgb:... ST</c> to set the default
    /// background.</summary>
    public static string SetBackground(Rgb c) => $"{Esc}]11;{RgbSpec(c)}{Esc}\\";

    /// <summary>
    /// Reset any palette / foreground / background overrides back to the
    /// terminal's own defaults: <c>OSC 104</c> (all palette entries),
    /// <c>OSC 110</c> (foreground), <c>OSC 111</c> (background). Written on
    /// exit so we never leave the user's terminal recoloured.
    /// </summary>
    public static string ResetColors() => $"{Esc}]104{Esc}\\{Esc}]110{Esc}\\{Esc}]111{Esc}\\";

    private static string RgbSpec(Rgb c) => $"rgb:{c.R:x2}{c.R:x2}/{c.G:x2}{c.G:x2}/{c.B:x2}{c.B:x2}";

    /// <summary>
    /// Build the full OSC burst that applies a theme's colour overrides:
    /// each palette entry, then foreground/background if present. Empty
    /// string if the theme overrides nothing.
    /// </summary>
    public static string BuildApplySequence(IReadOnlyDictionary<int, Rgb> palette, Rgb? foreground, Rgb? background)
    {
        if (palette.Count == 0 && foreground is null && background is null) return "";
        var sb = new StringBuilder();
        foreach (var (index, colour) in palette)
            sb.Append(SetPaletteColor(index, colour));
        if (foreground is { } fg) sb.Append(SetForeground(fg));
        if (background is { } bg) sb.Append(SetBackground(bg));
        return sb.ToString();
    }
}
