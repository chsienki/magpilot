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
        // its own flush failures via Console.WriteLine (NOT
        // Console.Error.WriteLine -- Blazor WASM surfaces stderr
        // writes as the yellow #blazor-error-ui banner via its
        // dotNetCriticalError handler). Neither path loops back
        // through this provider today, but we guard defensively
        // against future code that might.
        if (categoryName.Contains(nameof(HubLoggerProvider), StringComparison.Ordinal)
            || categoryName.Contains(nameof(HubLogClient), StringComparison.Ordinal))
        {
            return NullLogger.Instance;
        }
        // Magpilot.* categories forward to the central log at whatever
        // level the runtime gate allows (Trace through Critical).
        if (categoryName.StartsWith("Magpilot.", StringComparison.Ordinal))
        {
            return new HubForwardingLogger(hubLog, categoryName, gate: true);
        }
        // Framework categories (Microsoft.*, System.*) forward at
        // Warning+ regardless of gate -- the renderer's render-tree
        // events at Debug are far too noisy (~10k per turn), but its
        // unhandled-exception logs at Error are exactly what we need
        // to diagnose a "yellow blazor-error-ui banner" crash from a
        // device that has no F12 console (a phone).
        return new HubForwardingLogger(hubLog, categoryName, gate: false);
    }

    public void Dispose() { /* HubLogClient owns its lifetime via DI */ }

    private sealed class HubForwardingLogger(HubLogClient hubLog, string category, bool gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level)
        {
            if (level == LogLevel.None) return false;
            // App categories obey the runtime gate; framework categories
            // are gated at Warning+ via the filter installed in
            // Magpilot.Web.Program -- here we just have to not block
            // them.
            return gate ? level >= LogLevelGate.MinLevel : true;
        }

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
