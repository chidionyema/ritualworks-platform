using Xunit;

namespace Haworks.RulesEngine.Integration;

[CollectionDefinition(Name)]
public class RulesEngineIntegrationCollection : ICollectionFixture<RulesEngineWebAppFactory>
{
    public const string Name = "RulesEngine Integration";
}
