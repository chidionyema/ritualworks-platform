using Haworks.Localization.Api;
using Haworks.Localization.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Haworks.BuildingBlocks.Extensions;

var builder = WebApplication.CreateBuilder(args);

// builder.AddServiceDefaults(); // Assuming this is available in BuildingBlocks

builder.Services.AddLocalizationService(builder.Configuration, builder.Environment);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .Enrich.FromLogContext();
});

var app = builder.Build();

if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LocalizationDbContext>();
    await db.Database.EnsureCreatedAsync(); // Simplified for now
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { }
