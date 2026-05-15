using Haworks.RulesEngine.Api.Domain;
using Haworks.RulesEngine.Api.Infrastructure;
using Haworks.BuildingBlocks.Behaviors;
using MediatR;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddMediatR(cfg => {
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
});
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

builder.Services.AddScoped<IRulesEvaluator, RulesEvaluator>();

var app = builder.Build();

app.MapControllers();

app.Run();

public partial class Program { }
