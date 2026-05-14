using Haworks.Payouts.Application;
using Haworks.Payouts.Application.Disbursements.Services;
using Haworks.Payouts.Application.Ledger.Commands.MatureFunds;
using Haworks.Payouts.Infrastructure;
using Haworks.Payouts.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using MediatR;
using Hangfire;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration.ReadFrom.Configuration(context.Configuration));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddJwksAuthentication(builder.Configuration);
builder.Services.AddHttpContextAccessor();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PayoutsDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await db.Database.MigrateWithRetryAsync(logger);
}
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();
if (app.Environment.IsDevelopment()) { app.UseHangfireDashboard(); }

using (var scope = app.Services.CreateScope())
{
    var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobManager.AddOrUpdate<IDisbursementService>("process-payouts", service => service.ProcessEligiblePayoutsAsync(), Cron.Daily);
    recurringJobManager.AddOrUpdate<IMediator>("mature-funds", mediator => mediator.Send(new MatureFundsCommand(), default), Cron.Hourly);
}
app.Run();
public partial class Program { }
