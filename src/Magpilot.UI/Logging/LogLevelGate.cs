using Microsoft.Extensions.Logging;

namespace Magpilot.UI.Logging;

/// <summary>
/// Runtime-mutable minimum log level for the SPA. The framework filter
/// installed by <c>Magpilot.Web.Program</c> evaluates <see cref="MinLevel"/>
/// on every log call, so changing it takes effect immediately across all
/// providers (default WASM console + <see cref="HubLoggerProvider"/>) with
/// no rebuild and no restart.
///
/// Sources that can set the level:
///   * <c>MainLayout</c> on first render: localStorage["magpilot.logLevel"]
///     and the <c>?logLevel=X</c> query param.
///   * The <c>/admin/logs</c> page: the verbosity dropdown.
///
/// Default is <see cref="LogLevel.Information"/> so prod stays quiet
/// until someone explicitly opts in -- e.g. to remote-debug a flaky
/// stream on a phone, set verbosity to <c>Trace</c> on /admin/logs,
/// reproduce, watch the rows roll in, set it back.
/// </summary>
public static class LogLevelGate
{
    public const string LocalStorageKey = "magpilot.logLevel";
    public const string QueryParamName  = "logLevel";

    private static LogLevel _minLevel = LogLevel.Information;

    /// <summary>
    /// Current minimum level. Loggers and filters consult this on every
    /// log call, so updates are picked up immediately without restart.
    /// </summary>
    public static LogLevel MinLevel
    {
        get => _minLevel;
        private set
        {
            if (_minLevel == value) return;
            _minLevel = value;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Raised whenever <see cref="MinLevel"/> changes. UI subscribers
    /// (e.g. the dropdown on /admin/logs) listen to keep their displayed
    /// value in sync if some other code path changed the level.
    /// </summary>
    public static event Action? Changed;

    public static void Set(LogLevel level) => MinLevel = level;

    /// <summary>
    /// Best-effort parse of a string like "Trace" / "debug" / "WARN" /
    /// "5" into a <see cref="LogLevel"/>. Returns null for unrecognized
    /// input so callers can fall back to whatever default they want.
    /// </summary>
    public static LogLevel? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Case-insensitive enum parse handles both names and ordinals.
        if (Enum.TryParse<LogLevel>(value.Trim(), ignoreCase: true, out var parsed))
        {
            // Enum.TryParse accepts "None" (= 6) which would silence
            // everything; treat it as a valid choice -- callers who
            // don't want None can guard it themselves.
            return parsed;
        }
        return value.Trim().ToLowerInvariant() switch
        {
            "warn"  => LogLevel.Warning,
            "err"   => LogLevel.Error,
            "info"  => LogLevel.Information,
            "fatal" => LogLevel.Critical,
            _       => null,
        };
    }
}
