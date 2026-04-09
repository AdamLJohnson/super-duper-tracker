using DisneylandClient;
using DisneylandClient.ApiClient;
using DisneylandClient.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Refit;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var restBaseUrl   = builder.Configuration["ApiConfig:RestBaseUrl"]
                    ?? throw new InvalidOperationException(
                        "ApiConfig:RestBaseUrl is not configured. " +
                        "Ensure appsettings.json is present and contains the correct value.");
var webSocketUrl  = builder.Configuration["ApiConfig:WebSocketUrl"]
                    ?? throw new InvalidOperationException(
                        "ApiConfig:WebSocketUrl is not configured. " +
                        "Ensure appsettings.json is present and contains the correct value.");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Configure Refit with case-insensitive JSON deserialization and string enum support.
var refitSettings = new RefitSettings
{
    ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    }),
};

// REST API Gateway base URL (ApiStack CDK output).
builder.Services
    .AddRefitClient<IThemeParksApi>(refitSettings)
    .ConfigureHttpClient(c => c.BaseAddress = new Uri(restBaseUrl));

// WebSocket API Gateway URL (WebSocketStack CDK output).
builder.Services.AddSingleton(new WebSocketService(new Uri(webSocketUrl)));

await builder.Build().RunAsync();
