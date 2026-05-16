using Haworks.BuildingBlocks.Extensions;
using Haworks.Realtime.Api;
using Haworks.Realtime.Api.Infrastructure.SignalR;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults (OTel, health checks, etc)
builder.AddServiceDefaults();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

builder.Services.AddPlatformAuthentication(builder.Configuration);

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
app.MapHub<NotificationHub>("/hubs/notifications");

app.Run();

public partial class Program { }
