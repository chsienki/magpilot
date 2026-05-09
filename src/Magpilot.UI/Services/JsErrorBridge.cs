using Microsoft.JSInterop;

namespace Magpilot.UI.Services;

/// <summary>
/// Static-method bridge for <c>window.onerror</c> and
/// <c>unhandledrejection</c> to reach the .NET-side <see cref="HubLogClient"/>.
/// JS calls <c>DotNet.invokeMethodAsync('Magpilot.UI', 'CaptureJsError', ...)</c>;
/// we forward to the singleton client which the SPA registers in DI at startup.
///
/// We can't use a regular instance method because the JS handlers fire from
/// anywhere -- there's no component scope to attach a
/// <see cref="DotNetObjectReference{T}"/> to. The static accessor is set
/// once during <c>Program.cs</c> build.
/// </summary>
public static class JsErrorBridge
{
    private static HubLogClient? _client;

    public static void Configure(HubLogClient client) => _client = client;

    [JSInvokable]
    public static Task CaptureJsError(string kind, string message, string? stack)
    {
        _client?.LogError(
            message:  $"[{kind}] {message}",
            stack:    stack,
            category: "browser");
        return Task.CompletedTask;
    }
}
