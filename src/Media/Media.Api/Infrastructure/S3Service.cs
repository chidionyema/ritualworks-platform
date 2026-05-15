namespace Haworks.Media.Api.Infrastructure;

public interface IS3Service
{
    string GeneratePreSignedUrl(string key, string mimeType);
}

public class S3Service : IS3Service
{
    public string GeneratePreSignedUrl(string key, string mimeType)
    {
        // Mocked pre-signed URL generation
        return $"https://mock-s3-bucket.s3.amazonaws.com/{key}?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Expires=3600";
    }
}
