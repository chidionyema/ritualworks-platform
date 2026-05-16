using Xunit;

namespace Haworks.Analytics.Integration;

[CollectionDefinition("Analytics Integration")]
public sealed class AnalyticsIntegrationCollection : ICollectionFixture<AnalyticsWebAppFactory>
{
}
