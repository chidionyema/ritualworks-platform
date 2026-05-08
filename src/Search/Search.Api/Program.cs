using Haworks.BuildingBlocks.Extensions;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/health", () => Results.Ok());

// One-shot bootstrap of the Meilisearch index settings. Wrapped in
// try/catch + warning so a transiently down Meilisearch on first deploy
// doesn't crash app boot — both apps come up alongside each other on Fly.
using (var scope = app.Services.CreateScope())
{
    var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");
    try
    {
        await index.EnsureSettingsAsync();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Meilisearch settings bootstrap failed; will retry on next cold start");
    }
}

app.Run();

public partial class Program { }
