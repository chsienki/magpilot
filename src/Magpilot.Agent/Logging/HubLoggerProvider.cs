using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;

namespace Magpilot.Agent.Logging;

/// <summary>
/// Forwards <see cref="LogLevel.Warning"/>+ events to the central hub at
/// <c>POST /api/log/batch</c> so we can debug agent-side issues in the
/// same place as SPA + sidecar errors. Buffers in a bounded channel; drops
/// oldest on overflow so a hub outage can't OOM the agent.
///
/// Configured via env:
///   MAGPILOT_HUB_URL    - e.g. http://192.168.1.149:7088 (no trailing /)
///   MAGPILOT_HUB_BEARER - the same secret the hub validates in PhoneBearerHandler
///   MAGPILOT_AGENT_NAME - source label; defaults to <see cref="Environment.MachineName"/>
///
/// If either of the hub URL/bearer is missing the provider becomes a no-op
/// (returns NullLogger) so a misconfigured agent still runs.
/// </summary>
public sealed class HubLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private const int QueueCapacity = 2000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly HttpClient? _http;
    private readonly string _source;
    private readonly Channel<LogEventDto>? _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _drainTask;
    private readonly bool _enabled;

    public HubLoggerProvider()
    {
        var hubUrl = Environment.GetEnvironmentVariable("MAGPILOT_HUB_URL");
        var bearer = Environment.GetEnvironmentVariable("MAGPILOT_HUB_BEARER");
        _source = Environment.GetEnvironmentVariable("MAGPILOT_AGENT_NAME")
            ?? Environment.MachineName;

        if (string.IsNullOrWhiteSpace(hubUrl) || string.IsNullOrWhiteSpace(bearer))
        {
            _enabled = false;
            return;
        }

        _enabled = true;
        _http = new HttpClient
        {
            BaseAddress = new Uri(hubUrl.TrimEnd('/') + "/"),
            // Short timeout so a wedged hub doesn't pile up retries forever.
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        _queue = Channel.CreateBounded<LogEventDto>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _drainTask = Task.Run(() => DrainLoopAsync(_cts.Token));
    }

    public ILogger CreateLogger(string categoryName) =>
        _enabled ? new HubForwardingLogger(this, categoryName)
                 : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose() => _ = DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_drainTask is not null)
        {
            try { await _drainTask; } catch { }
        }
        _http?.Dispose();
        _cts.Dispose();
    }

    internal void Enqueue(string level, string category, string message, Exception? ex)
    {
        if (!_enabled || _queue is null) return;
        _queue.Writer.TryWrite(new LogEventDto(
            Source:    _source,
            Level:     level,
            Category:  category,
            Message:   message,
            Stack:     ex?.ToString(),
            SessionId: null,
            Extra:     null,
            UserAgent: null,
            Url:       null));
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        if (_queue is null || _http is null) return;
        var batch = new List<LogEventDto>(64);
        while (!ct.IsCancellationRequested)
        {
            batch.Clear();
            try
            {
                var first = await _queue.Reader.ReadAsync(ct);
                batch.Add(first);
            }
            catch (OperationCanceledException) { return; }

            while (batch.Count < 64 && _queue.Reader.TryRead(out var more))
                batch.Add(more);

            try
            {
                var payload = new LogEventBatch(batch.ToArray());
                using var resp = await _http.PostAsJsonAsync("api/log/batch", payload, ct);
                // Don't surface failures: this would feedback-loop into our
                // own log pipeline. The drop-oldest channel is the safety net.
            }
            catch { /* see comment above */ }

            try { await Task.Delay(FlushInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private sealed class HubForwardingLogger(HubLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;

        public void Log<TState>(LogLevel level, EventId _, TState state, Exception? ex,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            // Skip our own forwarder's egress noise to avoid
            // logging-of-logging spirals.
            if (category.Contains(nameof(HubLoggerProvider), StringComparison.Ordinal)) return;
            var levelName = level switch
            {
                LogLevel.Warning  => "Warning",
                LogLevel.Error    => "Error",
                LogLevel.Critical => "Critical",
                _                 => level.ToString(),
            };
            provider.Enqueue(levelName, category, formatter(state, ex), ex);
        }
    }
}

// --- Wire-format DTOs (must match Magpilot.Hub.Logging.LogModels) ---

public sealed record LogEventDto(
    string Source,
    string Level,
    string? Category,
    string Message,
    string? Stack,
    string? SessionId,
    JsonElement? Extra,
    string? UserAgent,
    string? Url);

public sealed record LogEventBatch(IReadOnlyList<LogEventDto> Events);
