using Haworks.Payouts.Application.Disbursements.Services;
using Haworks.Payouts.Application.Ledger.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using MediatR;
using FluentValidation;
using Haworks.BuildingBlocks.Behaviors;

namespace Haworks.Payouts.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg => {
            cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        });
        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<IDisbursementService, DisbursementService>();
        return services;
    }
}
