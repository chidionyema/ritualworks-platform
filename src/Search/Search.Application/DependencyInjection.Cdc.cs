using Haworks.Search.Application.Consumers;
using MassTransit;

namespace Haworks.Search.Application;

public static class CdcRegistration
{
    public static void RegisterCdcConsumers(IRegistrationConfigurator cfg)
    {
        cfg.AddConsumer<IndexableEntityChangedConsumer>();
    }
}
