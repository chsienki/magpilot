using Magpilot.UI.Abstractions;
using Magpilot.UI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Magpilot.Web;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

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

var host = builder.Build();

// Resolve the singleton once and stash it on the static JS-error bridge so
// window.onerror / unhandledrejection handlers can reach it without a
// per-component DotNetObjectReference.
JsErrorBridge.Configure(host.Services.GetRequiredService<HubLogClient>());

await host.RunAsync();
