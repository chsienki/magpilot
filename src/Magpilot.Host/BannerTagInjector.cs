using System.Text;

namespace Magpilot.Host;

/// <summary>
/// Appends a short tag to copilot's startup banner as it streams past, so a
/// magpilot-wrapped session is visibly branded. copilot opens an interactive
/// session with a line like <c>Copilot v1.0.76-3 uses AI.</c> (drawn in a
/// fixed grey); this injector watches for the stable <c>uses AI.</c> phrase
/// and, the first time it sees it, emits the tag immediately after -- e.g.
/// <c>Copilot v1.0.76-3 uses AI. (Magpilot v0.1.13)</c>. The tag carries no
/// colour of its own, so it inherits whatever the banner had active (the grey)
/// and blends in.
///
/// <para>The match is incremental and latches after the first hit: an anchor
/// split across read buffers is handled, and later occurrences of the phrase
/// in conversation text are left alone. Every byte copilot emitted passes
/// through unchanged -- the injector only inserts the tag, never rewrites or
/// drops output.</para>
/// </summary>
internal sealed class BannerTagInjector
{
    // Version-independent slice of copilot's startup banner. The version in
    // front of it changes every release, so the anchor starts after it. Its
    // first byte ('u') occurs nowhere else within it, which is what lets the
    // naive restart below stay correct without a full KMP table. If copilot
    // ever rewords the banner this simply no-ops -- the feature is cosmetic.
    private static readonly byte[] Anchor = "uses AI."u8.ToArray();

    private readonly byte[] _tag;
    private int _matched;
    private bool _done;

    public BannerTagInjector(string tag) => _tag = Encoding.UTF8.GetBytes(tag);

    /// <summary>Transform one chunk of copilot output, inserting the tag after
    /// the first occurrence of the banner anchor. A partial anchor at the end
    /// of a chunk carries over to the next call.</summary>
    public byte[] Transform(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length + _tag.Length);
        foreach (var b in input)
        {
            output.Add(b);
            if (_done) continue;

            if (b == Anchor[_matched])
            {
                if (++_matched == Anchor.Length)
                {
                    output.AddRange(_tag);
                    _done = true;
                }
            }
            else
            {
                // Reset, but let the mismatching byte start a fresh match so
                // e.g. "uuses AI." still anchors on the second 'u'.
                _matched = b == Anchor[0] ? 1 : 0;
            }
        }
        return output.ToArray();
    }
}
