using Xunit;

namespace Haworks.Search.Integration;

/// <summary>
/// Shared xUnit collection for every Search.Integration test class.
/// Forces them to run sequentially against ONE
/// <see cref="SearchWebAppFactory"/> (one Testcontainers Meilisearch,
/// one WireMock catalog stub, one MassTransit harness). Without this,
/// xUnit runs each test class in parallel and the GitHub Actions runner
/// struggles to stand up multiple containers concurrently — same root
/// cause as the Catalog and Payments collections.
/// </summary>
[CollectionDefinition("Search Integration")]
public sealed class SearchIntegrationCollection : ICollectionFixture<SearchWebAppFactory>
{
}
