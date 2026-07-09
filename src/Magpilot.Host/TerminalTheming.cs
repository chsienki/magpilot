using System.Text;

namespace Magpilot.Host;

/// <summary>
/// Applies the local client's terminal theming to a child copilot process,
/// shared by both launch paths: the ConPTY host (<see cref="PtyHost"/>) and
/// the transparent direct-exec passthrough (<c>--magpilot-skip-check</c> and
/// the agent-unreachable fallback).
///
/// <para>The two paths differ only in background detection. Under a ConPTY
/// copilot's own OSC 11 probe can't reach the outer terminal, so the host
/// probes and passes the answer via <c>COLORFGBG</c>. In direct-exec copilot
/// inherits the real terminal and its own probe works, so we only pin
/// <c>COLORFGBG</c> when the user has explicitly forced dark/light. Palette
/// overrides and the GitHub-theme flag apply identically to both.</para>
/// </summary>
internal static class TerminalTheming
{
    /// <summary>
    /// Add the theming env hints to a child's environment. <paramref name="isDark"/>
    /// is the resolved background (from a ConPTY probe, or an explicit config
    /// pin) or null to leave <c>COLORFGBG</c> unset -- appropriate when
    /// copilot can detect the background itself.
    /// </summary>
    public static void PopulateChildEnv(IDictionary<string, string> env, TerminalThemeConfig theme, bool? isDark)
    {
        if (isDark is { } dark)
            env["COLORFGBG"] = TerminalColor.ColorFgBg(dark);
        if (theme.EnableGithubTheme)
            env["COPILOT_GITHUB_THEME"] = "1";
    }

    /// <summary>
    /// The background to pin without probing: an explicit <c>dark</c>/<c>light</c>
    /// config value, or null for <c>auto</c> (let the caller probe, or let
    /// copilot detect natively).
    /// </summary>
    public static bool? PinnedBackground(TerminalThemeConfig theme) => theme.Background switch
    {
        BackgroundMode.Dark => true,
        BackgroundMode.Light => false,
        _ => null,
    };

    /// <summary>
    /// Push any configured palette / foreground / background overrides to the
    /// real terminal. Returns true if something was written (so the caller
    /// knows to call <see cref="ResetPalette"/> on exit). No-op when nothing
    /// is configured or output is redirected.
    /// </summary>
    public static bool ApplyPalette(TerminalThemeConfig theme)
    {
        if (Console.IsOutputRedirected) return false;
        var seq = TerminalColor.BuildApplySequence(theme.Palette, theme.Foreground, theme.BackgroundColor);
        if (seq.Length == 0) return false;
        WriteStdout(seq);
        return true;
    }

    /// <summary>Reset palette / foreground / background overrides back to the
    /// terminal's defaults. Best-effort; safe to call on shutdown.</summary>
    public static void ResetPalette()
    {
        if (Console.IsOutputRedirected) return;
        try { WriteStdout(TerminalColor.ResetColors()); }
        catch { /* best-effort on shutdown */ }
    }

    private static void WriteStdout(string sequence)
    {
        var so = Console.OpenStandardOutput();
        var bytes = Encoding.ASCII.GetBytes(sequence);
        so.Write(bytes, 0, bytes.Length);
        so.Flush();
    }
}
