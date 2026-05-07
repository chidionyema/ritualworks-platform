using Haworks.BuildingBlocks.Extensions;
using Haworks.Content.Infrastructure;
using Haworks.Content.Application;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & OTel
builder.AddServiceDefaults();

// Add Infrastructure & Application
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddApplication();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Authorization policy for content controllers — referenced by
// [Authorize(Policy = "ContentUploader")] on ContentController. Tests stamp
// the role via the shared TestAuthenticationHandler; production grants it
// via the upstream identity-svc-issued JWT claims.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ContentUploader", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("ContentUploader", "Admin"));
});

builder.Host.UseSerilog((context, loggerConfiguration) => {
    loggerConfiguration.ReadFrom.Configuration(context.Configuration);
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

public partial class Program { }
