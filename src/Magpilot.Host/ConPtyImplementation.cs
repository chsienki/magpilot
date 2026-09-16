using Porta.Pty;

namespace Magpilot.Host;

internal enum ConPtyImplementation
{
    AppLocal,
    System,
}

internal static class ConPtySelector
{
    private static readonly object s_gate = new();
    private static ConPtyImplementation? s_configured;

    public static string Configure()
    {
        if (!OperatingSystem.IsWindows())
            return "posix";

        var requested = Parse(Environment.GetEnvironmentVariable("MAGPILOT_CONPTY"));
        lock (s_gate)
        {
            if (s_configured is { } configured)
            {
                if (configured != requested)
                {
                    throw new InvalidOperationException(
                        $"ConPTY is already configured as {Name(configured)}; " +
                        $"cannot switch to {Name(requested)} in the same process.");
                }
                return PtyProvider.PseudoConsoleImplementation;
            }

            s_configured = requested;
            Environment.SetEnvironmentVariable(
                "PORTAPTY_CONPTY",
                requested == ConPtyImplementation.AppLocal ? "oob" : "inbox");

            var actual = PtyProvider.PseudoConsoleImplementation;
            var expected = requested == ConPtyImplementation.AppLocal ? "oob" : "inbox";
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Requested ConPTY implementation '{Name(requested)}', " +
                    $"but Porta.Pty resolved '{actual}'.");
            }
            return actual;
        }
    }

    internal static ConPtyImplementation Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "app-local" or "applocal" or "oob" => ConPtyImplementation.AppLocal,
            "system" or "inbox" or "in-box" => ConPtyImplementation.System,
            _ => throw new ArgumentException(
                $"Unknown MAGPILOT_CONPTY value '{value}'. Valid values: app-local, system."),
        };

    public static string Name(ConPtyImplementation implementation) =>
        implementation == ConPtyImplementation.AppLocal ? "app-local" : "system";
}
