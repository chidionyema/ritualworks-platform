using System.Net;
using FluentAssertions;
using Haworks.BuildingBlocks.Vault;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Vault;

public sealed class VaultAppRoleAuthenticatorTests
{
    private static HttpClient CreateFakeClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new HttpClient(new FakeHandler(handler)) { BaseAddress = new Uri("http://localhost:8200") };
    }

    private static string VaultLoginJson(long leaseDuration) =>
        "{\"auth\":{\"client_token\":\"hvs.test-token\",\"lease_duration\":" + leaseDuration + ",\"renewable\":true}}";

    [Fact]
    public async Task LoginAsync_ZeroLeaseDuration_DefaultsTo24Hours()
    {
        using var http = CreateFakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(VaultLoginJson(0))
        });

        var factory = new FakeHttpClientFactory(http);
        var authWithFactory = new VaultAppRoleAuthenticator(factory);

        var result = await authWithFactory.LoginAsync("http://localhost:8200", "role-id", "secret-id");

        result.LeaseDuration.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task LoginAsync_NormalLeaseDuration_ReturnsAsIs()
    {
        using var http = CreateFakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(VaultLoginJson(3600))
        });

        var factory = new FakeHttpClientFactory(http);
        var auth = new VaultAppRoleAuthenticator(factory);

        var result = await auth.LoginAsync("http://localhost:8200", "role-id", "secret-id");

        result.LeaseDuration.Should().Be(TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public async Task LoginAsync_MissingClientToken_ThrowsInvalidOperation()
    {
        using var http = CreateFakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"auth":{"lease_duration":3600}}""")
        });

        var factory = new FakeHttpClientFactory(http);
        var auth = new VaultAppRoleAuthenticator(factory);

        var act = () => auth.LoginAsync("http://localhost:8200", "role-id", "secret-id");
        await act.Should().ThrowAsync<Exception>();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
