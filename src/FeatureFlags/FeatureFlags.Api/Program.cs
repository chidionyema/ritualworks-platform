using Haworks.FeatureFlags.Api.Application;
using Haworks.FeatureFlags.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MassTransit;
using Haworks.BuildingBlocks.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("featureflags") 
    ?? "Host=localhost;Database=featureflags;Username=postgres;Password=postgres";

builder.Services.AddDbContext<FeatureFlagsDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "featureflags"));
});

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("redis");
});

builder.Services.AddMassTransit(mt =>
{
    mt.AddEntityFrameworkOutbox<FeatureFlagsDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    mt.UsingInMemory((context, cfg) =>
    {
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddApplication();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapDefaultEndpoints();
app.MapControllers();

app.Run();

public partial class Program { }
