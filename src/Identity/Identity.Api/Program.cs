using Haworks.BuildingBlocks.Extensions;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & OTel (lifted from Aspire ServiceDefaults)
builder.AddServiceDefaults();

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Serilog with explicit console sink. Avoid ReadFrom.Configuration here —
// when the appsettings shape isn't exactly what Serilog.Settings.Configuration
// expects it silently swallows ALL log output (Kestrel "Now listening" included)
// instead of falling back to defaults, which looks identical to a hang.
builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()  // explicit fallback — guarantees logs surface
        .Enrich.FromLogContext();
});

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
