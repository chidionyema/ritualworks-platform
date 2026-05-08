using Haworks.BuildingBlocks.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/health", () => Results.Ok());

app.Run();

public partial class Program { }
