using Clawpilot.UI.Abstractions;
using Clawpilot.UI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Clawpilot.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<IHubAuthProvider, WebHubAuthProvider>();
builder.Services.AddScoped(sp =>
{
    var auth = sp.GetRequiredService<IHubAuthProvider>();
    var http = new HttpClient
    {
        // Cookie auth + same origin -> base address is the host page.
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    };
    return new HubClient(http, auth);
});

await builder.Build().RunAsync();
