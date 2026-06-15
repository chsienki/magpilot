using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Magpilot.UI.Abstractions;

namespace Magpilot.UI.Services;

/// <summary>
/// Fire-and-forget client that posts log events to the hub's
/// <c>POST /api/log[/batch]</c> endpoint. Buffers internally so the UI
/// thread never blocks on a network call -- a background drain task
/// flushes batches to the hub as fast as it can, dropping the oldest
/// entries when the queue overflows so a flaky hub can't OOM the SPA.
///
/// The SPA wires three sources into this client:
///   * <see cref="Magpilot.UI.Logging.HubLoggerProvider"/> -- mirrors
///     <see cref="ILogger"/> events whose level passes
///     <see cref="Magpilot.UI.Logging.LogLevelGate.MinLevel"/>.
///     Only categories under <c>Magpilot.*</c> are forwarded so a
///     verbose toggle doesn't flood the central log with framework
///     render-tree noise.
///   * <c>error-capture.js</c> -- the browser's <c>window.onerror</c>
///     and <c>unhandledrejection</c> handlers.
///   * Direct calls from app code via <see cref="LogError"/> /
///     <see cref="LogWarning"/> for known-bad branches.
/// </summary>
public sealed class HubLogClient : IAsyncDisposable
{
    private const int QueueCapacity = 1000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly IHubAuthProvider _auth;
    private readonly Channel<LogEventDto> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drainTask;
    private string? _userAgent;
    private string? _currentUrl;

    public HubLogClient(HttpClient http, IHubAuthProvider auth)
    {
        _http = http;
        _auth = auth;
        _queue = Channel.CreateBounded<LogEventDto>(new BoundedChannelOptions(QueueCapacity)
        {
            // Drop the oldest waiting event when full so a hub outage doesn't
            // cap-and-stall this whole pipeline. We log to console as a last
            // resort so the dropped event isn't completely invisible.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _drainTask = Task.Run(() => DrainLoopAsync(_cts.Token));
    }

    /// <summary>Set once at app start so every subsequent event includes it.</summary>
    public void SetUserAgent(string ua) => _userAgent = ua;

    /// <summary>Update on each navigation so events know which page raised them.</summary>
    public void SetCurrentUrl(string? url) => _currentUrl = url;

    public void LogError(string message, string? stack = null, string? category = null,
                         string? sessionId = null, object? extra = null) =>
        Enqueue("Error", message, stack, category, sessionId, extra);

    public void LogWarning(string message, string? category = null,
                           string? sessionId = null, object? extra = null) =>
        Enqueue("Warning", message, null, category, sessionId, extra);

    public void Log(string level, string message, string? stack = null, string? category = null,
                    string? sessionId = null, object? extra = null) =>
        Enqueue(level, message, stack, category, sessionId, extra);

    private void Enqueue(string level, string message, string? stack, string? category,
                         string? sessionId, object? extra)
    {
        if (string.IsNullOrEmpty(message)) return;
        JsonElement? extraEl = null;
        if (extra is not null)
        {
            try { extraEl = JsonSerializer.SerializeToElement(extra); }
            catch { /* non-serializable; drop the extra payload, keep the message */ }
        }
        var dto = new LogEventDto(
            Source:    "spa",
            Level:     level,
            Category:  category,
            Message:   message,
            Stack:     stack,
            SessionId: sessionId,
            Extra:     extraEl,
            UserAgent: _userAgent,
            Url:       _currentUrl);
        // TryWrite is the non-blocking variant; with DropOldest semantics it
        // will succeed unless the channel is closed.
        _queue.Writer.TryWrite(dto);
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        var batch = new List<LogEventDto>(64);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                batch.Clear();
                try
                {
                    var first = await _queue.Reader.ReadAsync(ct);
                    batch.Add(first);
                }
                catch (OperationCanceledException) { break; }

                // Greedily pull anything else that's already waiting before we
                // open the HTTP socket. Caps at 64 to keep payloads small.
                while (batch.Count < 64 && _queue.Reader.TryRead(out var more))
                    batch.Add(more);

                try
                {
                    var payload = batch.Count == 1
                        ? (object)batch[0]
                        : new LogEventBatch(batch.ToArray());
                    var endpoint = batch.Count == 1 ? "api/log" : "api/log/batch";
                    using var resp = await _http.PostAsJsonAsync(endpoint, payload, ct);
                    // Don't block on EnsureSuccessStatusCode -- we drop on failure to
                    // avoid feedback loops where logging-failure-logging spirals.
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Console.WriteLine (NOT Console.Error.WriteLine):
                    // Blazor WASM routes Console.Error through its
                    // dotNetCriticalError handler, which both
                    // console.error's the message AND shows the yellow
                    // #blazor-error-ui banner. A transient hub-flush
                    // failure is not a fatal app error -- we recover
                    // via DropOldest on the channel -- so it must go
                    // to stdout instead.
                    Console.WriteLine($"[HubLogClient] flush failed: {ex.Message}");
                }

                // Brief settle so we coalesce bursty events into a single batch.
                try { await Task.Delay(FlushInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (Exception ex)
        {
            // Console.WriteLine, not Console.Error.WriteLine -- see the
            // comment on the per-flush failure path above.
            Console.WriteLine($"[HubLogClient] drain crashed: {ex}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _drainTask; } catch { }
        _cts.Dispose();
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
