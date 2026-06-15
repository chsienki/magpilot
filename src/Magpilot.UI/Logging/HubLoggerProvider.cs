using Magpilot.UI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magpilot.UI.Logging;

/// <summary>
/// <see cref="ILoggerProvider"/> for the SPA. Mirrors the agent-side
/// provider in <c>Magpilot.Agent.Logging</c>: every <see cref="ILogger"/>
/// call that passes <see cref="LogLevelGate.MinLevel"/> is forwarded to
/// the central log via <see cref="HubLogClient"/>, so any component can
/// write trace/debug/info breadcrumbs that show up in <c>/admin/logs</c>
/// without rebuilding.
///
/// The default WASM <c>WebAssemblyConsoleLogger</c> stays registered, so
/// every gated event also appears in the browser's F12 console -- the
/// runtime filter in <c>Magpilot.Web.Program</c> applies to it too.
/// </summary>
public sealed class HubLoggerProvider(HubLogClient hubLog) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        // Skip our own forwarder's egress noise. HubLogClient logs
        // its own flush failures via Console.Error.WriteLine, so even
        // a flaky hub can't loop through this provider.
        if (categoryName.Contains(nameof(HubLoggerProvider), StringComparison.Ordinal)
            || categoryName.Contains(nameof(HubLogClient), StringComparison.Ordinal))
        {
            return NullLogger.Instance;
        }
        // Only Magpilot-namespaced categories forward to the hub log.
        // Framework events (Microsoft.AspNetCore.*, System.*) still go
        // to the F12 console via the standard WebAssemblyConsoleLogger,
        // but at Information+ -- otherwise toggling the gate to Trace
        // floods the central log with render-tree debug events.
        if (!categoryName.StartsWith("Magpilot.", StringComparison.Ordinal))
        {
            return NullLogger.Instance;
        }
        return new HubForwardingLogger(hubLog, categoryName);
    }

    public void Dispose() { /* HubLogClient owns its lifetime via DI */ }

    private sealed class HubForwardingLogger(HubLogClient hubLog, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) =>
            level != LogLevel.None && level >= LogLevelGate.MinLevel;

        public void Log<TState>(LogLevel level, EventId _, TState state, Exception? ex,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var levelName = level switch
            {
                LogLevel.Trace       => "Trace",
                LogLevel.Debug       => "Debug",
                LogLevel.Information => "Information",
                LogLevel.Warning     => "Warning",
                LogLevel.Error       => "Error",
                LogLevel.Critical    => "Critical",
                _                    => level.ToString(),
            };
            hubLog.Log(levelName, formatter(state, ex), stack: ex?.ToString(), category: category);
        }
    }
}
