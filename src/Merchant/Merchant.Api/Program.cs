using Haworks.Merchant.Application;
using Haworks.Merchant.Infrastructure;
using Haworks.Merchant.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.Extensions;
using Haworks.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components
builder.AddServiceDefaults();

// Add Serilog
builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration));

// Add layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddJwksAuthentication(builder.Configuration);
builder.Services.AddHttpContextAccessor();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Auto-apply EF migrations at startup. 
if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await db.Database.MigrateWithRetryAsync(logger);
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();

app.Run();

public partial class Program { }
