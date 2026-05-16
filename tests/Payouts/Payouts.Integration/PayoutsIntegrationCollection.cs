using Xunit;
namespace Haworks.Payouts.Integration;
[CollectionDefinition(nameof(PayoutsIntegrationTestDefinition))]
public class PayoutsIntegrationTestDefinition : ICollectionFixture<PayoutsWebAppFactory> { }
