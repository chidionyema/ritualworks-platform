using Xunit;

namespace Haworks.Localization.Integration;

[CollectionDefinition(Name)]
public class LocalizationIntegrationCollection : ICollectionFixture<LocalizationWebAppFactory>
{
    public const string Name = "LocalizationIntegration";
}
