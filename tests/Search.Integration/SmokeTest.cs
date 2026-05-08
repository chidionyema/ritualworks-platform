using Xunit;

namespace Haworks.Search.Integration;

public sealed class SmokeTest : IClassFixture<SearchWebAppFactory>
{
    private readonly SearchWebAppFactory _factory;

    public SmokeTest(SearchWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_endpoint_returns_success()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }
}
