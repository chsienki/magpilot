using Magpilot.UI.Abstractions;
using Magpilot.UI.Logging;
using Magpilot.UI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using Magpilot.Web;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

// Logging: keep the framework filter permissive (Trace) and gate every
// provider with a runtime-mutable filter that reads from LogLevelGate.
// The runtime gate applies ONLY to our own Magpilot.* categories so a
// verbose toggle doesn't unleash the renderer's render-tree debug noise
// (~10k events per turn). Framework categories (Microsoft.*, System.*)
// stay at Warning+ -- noisy enough is silent, but framework error
// reports (especially the renderer's unhandled-exception logs that
// drive the yellow blazor-error-ui banner) reach /admin/logs so we
// can debug the actual crash from any device.
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.Logging.AddFilter((category, level) =>
{
    if (category is not null && category.StartsWith("Magpilot.", StringComparison.Ordinal))
        return level >= LogLevelGate.MinLevel;
    return level >= LogLevel.Warning;
});

builder.Services.AddSingleton<IHubAuthProvider, WebHubAuthProvider>();
builder.Services.AddScoped(sp =>
{
    var auth = sp.GetRequiredService<IHubAuthProvider>();
    var js = sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>();
    var http = new HttpClient(new IncludeCredentialsHandler(js))
    {
        // Cookie auth + same origin -> base address is the host page.
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    };
    return new HubClient(http, auth);
});

// Central log forwarder: dedicated HttpClient (separate from HubClient so a
// stuck data fetch can't starve log egress and vice-versa). Singleton so the
// in-memory queue is shared across components.
builder.Services.AddSingleton(sp =>
{
    var auth = sp.GetRequiredService<IHubAuthProvider>();
    var js = sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>();
    var http = new HttpClient(new IncludeCredentialsHandler(js))
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    };
    return new HubLogClient(http, auth);
});

// Bridge ILogger<T> calls into the central log via HubLogClient. The default
// WebAssemblyConsoleLogger stays registered, so the same events still appear
// in the browser F12 console -- both surfaces share the LogLevelGate filter.
builder.Services.AddSingleton<ILoggerProvider, Magpilot.UI.Logging.HubLoggerProvider>();

var host = builder.Build();

// Resolve the singleton once and stash it on the static JS-error bridge so
// window.onerror / unhandledrejection handlers can reach it without a
// per-component DotNetObjectReference.
JsErrorBridge.Configure(host.Services.GetRequiredService<HubLogClient>());

await host.RunAsync();
