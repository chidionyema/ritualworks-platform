using Xunit;

namespace Haworks.Catalog.Integration;

/// <summary>
/// Shared xUnit collection for every Catalog.Integration test class.
/// Forces them to run sequentially against ONE
/// <see cref="CatalogWebAppFactory"/> (one Testcontainers Postgres,
/// one MassTransit harness). Without this shared collection, xUnit
/// runs each test class in parallel — and the GitHub Actions runner
/// can't reliably stand up two Postgres containers at once
/// (intermittent "Failed to connect to 127.0.0.1:&lt;port&gt;" failures).
/// </summary>
[CollectionDefinition("Catalog Integration")]
public sealed class CatalogIntegrationCollection : ICollectionFixture<CatalogWebAppFactory>
{
}
