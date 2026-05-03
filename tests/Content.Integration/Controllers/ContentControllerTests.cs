using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Haworks.Content.Api.Models;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Domain.ValueObjects;
using Haworks.Content.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Testing;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Application.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Content.Integration.Controllers;

[Trait("Category", "Integration")]
[Trait("Controller", "Content")]
public class ContentControllerTests : IClassFixture<ContentWebAppFactory>, IAsyncLifetime
{
    private readonly HttpClient _client;
    private readonly ContentWebAppFactory _factory;
    private readonly ITestOutputHelper _output;

    public ContentControllerTests(ContentWebAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _client = _factory.WithTestAuth().CreateClient();
    }

    public async Task InitializeAsync() => await _factory.EnsureSchemaAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private MultipartFormDataContent CreateFileUploadContent(string fieldName, string fileName, string contentType, byte[] data)
    {
        var content = new MultipartFormDataContent();
        var byteContent = new ByteArrayContent(data);
        byteContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        content.Add(byteContent, fieldName, fileName);
        return content;
    }

    private async Task<string> DebugResponse(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"Status: {response.StatusCode}\nContent: {content}");
        return content;
    }

    [Fact]
    public async Task UploadFile_ValidFile_ReturnsCreatedContent()
    {
        // Create minimal valid JPEG file bytes
        var jpegBytes = Convert.FromBase64String(
           "/9j/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQCgwSExIQEw8QEBD/yQALCAABAAEBAREA/8wABgAQEAX/2gAIAQEAAD8A0I8g/9k="
           );

        var fileContent = CreateFileUploadContent(
            "file",
            "test.jpg",
            "image/jpeg",
            jpegBytes);

        var response = await _client.PostAsync(
            "/api/v1/content/upload?entityId=" + Guid.NewGuid(),
            fileContent);

        if (!response.IsSuccessStatusCode)
        {
            await DebugResponse(response);
        }

        response.Should().HaveStatusCode(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UploadFile_InvalidFileType_RejectsUpload()
    {
        var fileContent = CreateFileUploadContent(
            "file",
            "test.exe",
            "application/x-msdownload",
            Encoding.UTF8.GetBytes("Malicious content"));

        var response = await _client.PostAsync("/api/v1/content/upload?entityId=" + Guid.NewGuid(), fileContent);
        response.Should().HaveStatusCode(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetContent_ExistingContent_ReturnsContentDto()
    {
        ContentEntity contentToCreate;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            var contentId = Guid.NewGuid();
            var entityId = Guid.NewGuid();
            contentToCreate = ContentEntity.Create(contentId, entityId, "documents", ContentType.Document);
            contentToCreate.SetStorageInfo("documents", "test-object", "test-blob", 1024);
            contentToCreate.SetFileInfo("test.pdf", "application/pdf", "pdf");
            contentToCreate.SetUrlInfo("/test/url", "/test/path");
            await dbContext.Contents.AddAsync(contentToCreate);
            await dbContext.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/v1/content/{contentToCreate.Id}");
        response.Should().HaveStatusCode(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetContent_NonExistingContent_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/v1/content/{Guid.NewGuid()}");
        response.Should().HaveStatusCode(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteContent_RemovesFromDatabase()
    {
        Guid contentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            var content = ContentEntity.Create(Guid.NewGuid(), Guid.NewGuid(), "test", ContentType.Other);
            content.SetStorageInfo("test", "test", "test-blob", 0);
            content.SetFileInfo("testfile.txt", "text/plain", "txt");
            await context.Contents.AddAsync(content);
            await context.SaveChangesAsync();
            contentId = content.Id;
        }

        var response = await _client.DeleteAsync($"/api/v1/content/{contentId}");
        response.Should().HaveStatusCode(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task InitChunkSession_ValidRequest_ReturnsCreatedSession()
    {
        var request = new ChunkSessionRequest(
            EntityId: Guid.NewGuid(),
            ChunkSize: 1024 * 1024 * 10,
            FileName: "largefile.mp4",
            ContentType: "video/mp4",
            TotalChunks: 10,
            TotalSize: 1024 * 1024 * 100
        );

        var response = await _client.PostAsJsonAsync("/api/v1/content/chunked/init", request);
        response.Should().HaveStatusCode(HttpStatusCode.Created);
    }

    [Fact]
    public async Task InitChunkSession_InvalidSize_ReturnsBadRequest()
    {
        var request = new ChunkSessionRequest(
            EntityId: Guid.NewGuid(),
            ChunkSize: 1024 * 1024,
            FileName: "test.mp4",
            ContentType: "video/mp4",
            TotalChunks: 0,
            TotalSize: 0
        );

        var response = await _client.PostAsJsonAsync("/api/v1/content/chunked/init", request);
        response.Should().HaveStatusCode(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadChunk_ValidChunk_StoresInTempStorage()
    {
        const int chunkSize = 1024 * 1024;
        var initResponse = await _client.PostAsJsonAsync("/api/v1/content/chunked/init",
            new ChunkSessionRequest(
                EntityId: Guid.NewGuid(),
                ChunkSize: chunkSize,
                FileName: "test.mp4",
                ContentType: "video/mp4",
                TotalChunks: 3,
                TotalSize: chunkSize * 3
            ));

        var session = await initResponse.Content.ReadFromJsonAsync<ChunkSessionResponse>();
        var chunkContent = CreateFileUploadContent(
            "chunkFile",
            "chunk.bin",
            "application/octet-stream",
            new byte[chunkSize]);

        var response = await _client.PostAsync(
            $"/api/v1/content/chunked/{session!.SessionId}/0",
            chunkContent);

        response.Should().HaveStatusCode(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CompleteChunkSession_ValidSession_AssemblesFile()
    {
        var entityId = Guid.NewGuid();
        var fileName = "test.mp4";
        var totalChunks = 3;
        var chunkSize = 1024 * 1024;
        var totalSize = totalChunks * chunkSize;

        var initResponse = await _client.PostAsJsonAsync(
            "/api/v1/content/chunked/init",
            new ChunkSessionRequest(
                EntityId: entityId,
                ChunkSize: chunkSize,
                FileName: fileName,
                ContentType: "video/mp4",
                TotalChunks: totalChunks,
                TotalSize: totalSize
            ));

        initResponse.EnsureSuccessStatusCode();
        var session = await initResponse.Content.ReadFromJsonAsync<ChunkSessionResponse>();

        var rng = new Random();
        for (int i = 0; i < totalChunks; i++)
        {
            var chunkData = new byte[chunkSize];
            rng.NextBytes(chunkData);

            using var chunkContent = CreateFileUploadContent(
                "chunkFile",
                $"chunk{i}.bin",
                "application/octet-stream",
                chunkData);

            using var chunkResponse = await _client.PostAsync(
                $"/api/v1/content/chunked/{session!.SessionId}/{i}",
                chunkContent);

            chunkResponse.EnsureSuccessStatusCode();
        }

        using var completeResponse = await _client.PostAsync($"/api/v1/content/chunked/complete/{session!.SessionId}", null);
        completeResponse.Should().HaveStatusCode(HttpStatusCode.Created);
    }

    [Fact]
    public async Task GetChunkSessionStatus_ValidSession_ReturnsProgress()
    {
        var initResponse = await _client.PostAsJsonAsync("/api/v1/content/chunked/init",
            new ChunkSessionRequest(
                EntityId: Guid.NewGuid(),
                ChunkSize: 1048576,
                FileName: "large.mp4",
                ContentType: "video/mp4",
                TotalChunks: 5,
                TotalSize: 5242880
            ));

        var session = await initResponse.Content.ReadFromJsonAsync<ChunkSessionResponse>();

        var response = await _client.GetAsync($"/api/v1/content/chunked/session/{session!.SessionId}");
        response.Should().HaveStatusCode(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CompleteChunkSession_IncompleteSession_ReturnsBadRequest()
    {
        const int chunkSize = 1024 * 1024;
        var initResponse = await _client.PostAsJsonAsync("/api/v1/content/chunked/init",
            new ChunkSessionRequest(
                EntityId: Guid.NewGuid(),
                ChunkSize: chunkSize,
                FileName: "largefile.mp4",
                ContentType: "video/mp4",
                TotalChunks: 3,
                TotalSize: chunkSize * 3
            ));

        var session = await initResponse.Content.ReadFromJsonAsync<ChunkSessionResponse>();

        var response = await _client.PostAsync(
            $"/api/v1/content/chunked/complete/{session!.SessionId}",
            null);

        response.Should().HaveStatusCode(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadChunk_InvalidSession_ReturnsNotFound()
    {
        var chunkContent = CreateFileUploadContent(
            "chunkFile",
            "chunk.bin",
            "application/octet-stream",
            new byte[1024]);

        var response = await _client.PostAsync(
            $"/api/v1/content/chunked/{Guid.NewGuid()}/0",
            chunkContent);

        response.Should().HaveStatusCode(HttpStatusCode.NotFound);
    }
}
