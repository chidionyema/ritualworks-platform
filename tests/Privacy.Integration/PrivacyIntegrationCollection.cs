using Xunit;

namespace Haworks.Privacy.Integration;

[CollectionDefinition("Privacy Integration")]
public sealed class PrivacyIntegrationCollection : ICollectionFixture<PrivacyWebAppFactory>
{
}
