using Haworks.Content.Domain.Entities;

namespace Haworks.Content.UnitTests.Helpers;

public static class DomainTestHelpers
{
    public static ContentEntity CreatePendingContentEntity(
        Guid? entityId = null,
        string entityType = "Product",
        ContentType contentType = ContentType.Image,
        string ownerUserId = "test-user",
        string fileName = "test-file.png",
        string contentTypeMime = "image/png",
        long expectedSize = 1024,
        UploadKind uploadKind = UploadKind.Single,
        string bucketName = "test-bucket",
        string objectKey = "test-user/abc/test-file.png",
        string? s3UploadId = null)
    {
        return ContentEntity.CreatePending(
            entityId: entityId ?? Guid.NewGuid(),
            entityType: entityType,
            contentType: contentType,
            ownerUserId: ownerUserId,
            fileName: fileName,
            contentTypeMime: contentTypeMime,
            expectedSize: expectedSize,
            uploadKind: uploadKind,
            bucketName: bucketName,
            objectKey: objectKey,
            s3UploadId: s3UploadId);
    }
}
