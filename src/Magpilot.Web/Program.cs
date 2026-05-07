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

await builder.Build().RunAsync();
