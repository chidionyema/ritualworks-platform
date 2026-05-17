using Xunit;

namespace Haworks.Merchant.Integration;

[CollectionDefinition("Merchant Integration")]
public sealed class MerchantIntegrationCollection : ICollectionFixture<MerchantWebAppFactory>
{
}
