using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Client;
using Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"];

// If no ApiBaseUrl configured (or empty), use the current browser origin
var baseUri = string.IsNullOrWhiteSpace(apiBaseUrl)
    ? new Uri(builder.HostEnvironment.BaseAddress)
    : new Uri(apiBaseUrl);

builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = baseUri
});

builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<AuthStateService>();

await builder.Build().RunAsync();
