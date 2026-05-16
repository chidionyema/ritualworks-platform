using Haworks.Scheduler.Application;
using Haworks.Scheduler.Infrastructure;
using Haworks.Scheduler.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Startup;
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Hangfire.Dashboard;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components
builder.AddServiceDefaults();

// Add Serilog
builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration));

// Add layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddStartupTaskRunner();

builder.Services.AddPlatformAuthentication(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks()
    .AddDbHealthCheck<Haworks.Scheduler.Infrastructure.Persistence.SchedulerDbContext>();

var app = builder.Build();

if (!app.Environment.IsEnvironment("Test"))
{
    var startupRunner = app.Services.GetRequiredService<StartupTaskRunner>();
    startupRunner.AddTask(async (sp, ct) =>
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulerDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await db.Database.MigrateWithRetryAsync(logger, ct);
    });
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();

// Configure Hangfire Dashboard — only in Development, with auth filter
if (app.Environment.IsDevelopment())
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new HangfireLocalRequestFilter() }
    });
}

app.Run();

public partial class Program { }

/// <summary>
/// Hangfire dashboard authorization filter that restricts access to authenticated users
/// or, in development, to local requests only.
/// </summary>
internal sealed class HangfireLocalRequestFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        // Allow local loopback in development; require authentication otherwise.
        var connection = httpContext.Connection;
        bool isLocal = connection.RemoteIpAddress != null
            && (connection.RemoteIpAddress.Equals(connection.LocalIpAddress)
                || System.Net.IPAddress.IsLoopback(connection.RemoteIpAddress));
        return isLocal && httpContext.User.IsInRole("admin");
    }
}
