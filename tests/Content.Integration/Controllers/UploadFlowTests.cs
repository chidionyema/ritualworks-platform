using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Haworks.Content.Application.DTOs;
using Haworks.Content.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haworks.Content.Integration.Controllers;

/// <summary>
/// End-to-end smoke tests for the presigned-URL upload pipeline against
/// LocalStack S3. Exercises the path the client sees: init → PUT bytes
/// directly to LocalStack via the presigned URL → complete → read.
/// </summary>
public sealed class UploadFlowTests : IClassFixture<ContentWebAppFactory>, IAsyncLifetime
{
    private readonly ContentWebAppFactory _factory;
    private readonly HttpClient _api;
    private readonly HttpClient _bare;

    public UploadFlowTests(ContentWebAppFactory factory)
    {
        _factory = factory;
        _api = factory.CreateClient();
        // A bare client (not the WAF one) so PUTs to the presigned URL go
        // out via the real HTTP stack to LocalStack, not through the
        // in-process TestServer.
        _bare = new HttpClient();
    }

    public Task InitializeAsync() => _factory.EnsureSchemaAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Init_returns_a_single_PUT_url_for_small_files()
    {
        var initResp = await _api.PostAsJsonAsync("/api/v1/content/uploads", new InitUploadRequestDto(
            EntityId: Guid.NewGuid(),
            EntityType: "Product",
            FileName: "thumbnail.png",
            ContentType: "image/png",
            TotalSize: 4 * 1024));

        initResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var init = await initResp.Content.ReadFromJsonAsync<InitUploadResultDto>();
        init.Should().NotBeNull();
        init!.Kind.Should().Be(UploadKind.Single);
        init.PutUrl.Should().NotBeNullOrEmpty();
        init.UploadId.Should().BeNull();
        init.PartUrls.Should().BeNull();
    }

    [Fact]
    public async Task Init_returns_multipart_part_urls_for_large_files()
    {
        // 32 MiB > the default 8 MiB SinglePutMaxBytes threshold → multipart.
        const long size = 32L * 1024 * 1024;
        var initResp = await _api.PostAsJsonAsync("/api/v1/content/uploads", new InitUploadRequestDto(
            EntityId: Guid.NewGuid(),
            EntityType: "Product",
            FileName: "video.mp4",
            ContentType: "video/mp4",
            TotalSize: size));

        initResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var init = await initResp.Content.ReadFromJsonAsync<InitUploadResultDto>();
        init.Should().NotBeNull();
        init!.Kind.Should().Be(UploadKind.Multipart);
        init.PutUrl.Should().BeNull();
        init.UploadId.Should().NotBeNullOrEmpty();
        init.PartUrls.Should().NotBeNull();
        init.PartUrls!.Count.Should().BeGreaterThan(1);
        init.PartUrls[0].PartNumber.Should().Be(1);
    }

    [Fact]
    public async Task Single_PUT_bytes_uploaded_to_presigned_url_succeed()
    {
        var fileBytes = MakeFakePngBytes(4 * 1024);
        var initResp = await _api.PostAsJsonAsync("/api/v1/content/uploads", new InitUploadRequestDto(
            EntityId: Guid.NewGuid(),
            EntityType: "Product",
            FileName: "smoke.png",
            ContentType: "image/png",
            TotalSize: fileBytes.Length));
        initResp.EnsureSuccessStatusCode();
        var init = await initResp.Content.ReadFromJsonAsync<InitUploadResultDto>();

        using var put = new HttpRequestMessage(HttpMethod.Put, init!.PutUrl);
        put.Content = new ByteArrayContent(fileBytes);
        put.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var putResp = await _bare.SendAsync(put);

        putResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the presigned URL must accept the PUT directly without going through content-svc");
    }

    [Fact]
    public async Task Abort_on_a_Pending_row_is_idempotent()
    {
        var initResp = await _api.PostAsJsonAsync("/api/v1/content/uploads", new InitUploadRequestDto(
            EntityId: Guid.NewGuid(),
            EntityType: "Product",
            FileName: "abandoned.png",
            ContentType: "image/png",
            TotalSize: 1024));
        initResp.EnsureSuccessStatusCode();
        var init = await initResp.Content.ReadFromJsonAsync<InitUploadResultDto>();

        var first = await _api.PostAsync($"/api/v1/content/uploads/{init!.ContentId}/abort", null);
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await _api.PostAsync($"/api/v1/content/uploads/{init.ContentId}/abort", null);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GetUploadStatus_returns_404_for_unknown_id()
    {
        var resp = await _api.GetAsync($"/api/v1/content/uploads/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Init_returns_401_for_unauthenticated_caller()
    {
        var noAuth = _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddAuthentication("Failing").AddScheme<
                    AuthenticationSchemeOptions, NoOpFailingHandler>("Failing", _ => { })))
            .CreateClient();

        var resp = await noAuth.PostAsJsonAsync("/api/v1/content/uploads", new InitUploadRequestDto(
            EntityId: Guid.NewGuid(),
            EntityType: "Product",
            FileName: "x.png",
            ContentType: "image/png",
            TotalSize: 1024));

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    private static byte[] MakeFakePngBytes(int sizeBytes)
    {
        var bytes = new byte[sizeBytes];
        bytes[0] = 0x89; bytes[1] = (byte)'P'; bytes[2] = (byte)'N'; bytes[3] = (byte)'G';
        bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
        return bytes;
    }

    private sealed class NoOpFailingHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Fail("forced"));
    }
}
