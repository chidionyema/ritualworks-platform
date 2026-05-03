using Haworks.Content.Domain.Entities;
using Haworks.Content.Domain.ValueObjects;

namespace Haworks.Content.UnitTests.Helpers;

public static class DomainTestHelpers
{
    public static ContentEntity CreateContentEntity(
        Guid? id = null,
        Guid? entityId = null,
        string entityType = "Product",
        ContentType contentType = ContentType.Image,
        string path = "/test/path",
        long fileSize = 1024,
        string bucketName = "test-bucket",
        string objectName = "test-object",
        string blobName = "test-blob",
        string fileName = "test-file",
        string url = "https://example.com/test")
    {
        var content = ContentEntity.Create(
            id ?? Guid.NewGuid(),
            entityId ?? Guid.NewGuid(),
            entityType,
            contentType);

        content.SetStorageInfo(bucketName, objectName, blobName, fileSize);
        content.SetFileInfo(fileName, string.Empty, string.Empty);
        content.SetUrlInfo(url, path);
        return content;
    }
}
