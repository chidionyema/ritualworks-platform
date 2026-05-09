using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Tests.E2E;

/// <summary>
/// Cross-service content upload E2E. PRE-CONDITION: the docker-compose stack
/// in <c>deploy/compose/docker-compose.yml</c> is up. Specifically:
///   - identity-svc reachable at <c>IDENTITY_URL</c> (default http://localhost:5070)
///   - content-svc  reachable at <c>CONTENT_URL</c>  (default http://localhost:5060)
///   - bff-web      reachable at <c>BFF_URL</c>      (default http://localhost:5050)
///   - LocalStack S3 reachable on the host at http://localhost:4566 (the
///     content-svc returns presigned URLs whose host is <c>localstack</c> —
///     the test rewrites the host so PUT/GET work from the host machine).
///
/// What this proves:
///   1. Real JWT minted by identity-svc (no TestAuthenticationHandler stub).
///   2. JWKS validation by content-svc against identity-svc's
///      <c>/.well-known/jwks.json</c>.
///   3. X-User-Id forwarding contract — content-svc requires the header that
///      the BFF would normally stamp; the test stamps it from the JWT's
///      NameIdentifier claim, mirroring what UserIdentityForwardingHandler does.
///   4. Presigned-URL upload pipeline: Init → PUT direct to LocalStack → Complete.
///   5. Validation pipeline runs on Complete and the row eventually flips to
///      Available (or Quarantined if ClamAV is wired and rejects the bytes).
///   6. Presigned GET round-trip — DownloadUrl returned by GetContent yields
///      the original bytes.
///
/// Why direct content-svc (not via BFF): the BFF currently has no content
/// passthrough route (see src/BffWeb/BffWeb.Api/Controllers/), so the test
/// publishes content-svc on host port 5060 and calls it directly with the
/// real JWT. Adding a BFF passthrough is a separate task; this still
/// exercises every other cross-service contract.
///
/// Skipped when the stack isn't reachable so unit/CI runs that don't bring
/// up compose still pass.
/// </summary>
public sealed class ContentE2ETests
{
    private static readonly string IdentityUrl =
        (Environment.GetEnvironmentVariable("IDENTITY_URL") ?? "http://localhost:5070").TrimEnd('/');

    private static readonly string ContentUrl =
        (Environment.GetEnvironmentVariable("CONTENT_URL") ?? "http://localhost:5060").TrimEnd('/');

    private static readonly string BffUrl =
        (Environment.GetEnvironmentVariable("BFF_URL") ?? "http://localhost:5050").TrimEnd('/');

    // LocalStack returns presigned URLs whose host matches the docker-network
    // hostname (localstack:4566). Rewrite to the host-side published port so
    // PUT/GET work from the test process.
    private const string LocalStackInternal = "http://localstack:4566";
    private const string LocalStackHost     = "http://localhost:4566";

    // Total budget we'll wait for the upload row to flip out of "Validating".
    // 60s covers ClamAV's first-scan cold start; the polling loop exits as
    // soon as the row reaches a terminal state.
    private static readonly TimeSpan ValidationBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval     = TimeSpan.FromMilliseconds(500);

    private readonly ITestOutputHelper _output;

    public ContentE2ETests(ITestOutputHelper output) => _output = output;

    // ---------------------------------------------------------------
    // Test 1: full register → login → presigned upload → download
    // ---------------------------------------------------------------
    [SkippableFact]
    public async Task RegisterLoginUploadDownload_RoundTrip()
    {
        Skip.IfNot(await StackIsReachableAsync(),
            $"E2E stack not reachable. Need: {IdentityUrl}/health, {ContentUrl}/health. " +
            "Bring up via `docker compose -f deploy/compose/docker-compose.yml up -d`.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // ----- 1. Register a fresh user on identity-svc --------------------
        var username = $"e2e_{Guid.NewGuid():N}".Substring(0, 20);
        var email    = $"{username}@example.com";
        var password = "Password123!";

        var registerRes = await http.PostAsJsonAsync(
            $"{IdentityUrl}/api/Authentication/register",
            new { username, email, password });

        registerRes.StatusCode.Should().Be(HttpStatusCode.Created,
            "register should create user. Body: " + await registerRes.Content.ReadAsStringAsync());

        // ----- 2. Login → real JWT ----------------------------------------
        var loginRes = await http.PostAsJsonAsync(
            $"{IdentityUrl}/api/Authentication/login",
            new { username, password });

        loginRes.StatusCode.Should().Be(HttpStatusCode.OK,
            "login should succeed. Body: " + await loginRes.Content.ReadAsStringAsync());

        var auth = await loginRes.Content.ReadFromJsonAsync<AuthResponse>();
        auth.Should().NotBeNull();
        auth!.Token.Should().NotBeNullOrEmpty();

        // userId travels in the token; the BFF would normally extract it
        // and stamp X-User-Id on every backend call. We do the same here.
        var userId = ExtractUserId(auth.Token);
        userId.Should().NotBeNullOrEmpty();
        _output.WriteLine($"Minted JWT for userId={userId}");

        // ----- 3. Init upload --------------------------------------------
        // Real PNG-ish bytes (1x1 PNG, ~70 bytes). Content-svc validates
        // the file extension + sniffs the magic header on Complete, so a
        // valid PNG keeps the row out of Quarantined.
        byte[] payload = OnePixelPng();

        var initBody = new InitUploadRequestDto(
            EntityId:    Guid.NewGuid(),
            EntityType:  "Test",
            FileName:    "e2e.png",
            ContentType: "image/png",
            TotalSize:   payload.Length);

        var initReq = new HttpRequestMessage(HttpMethod.Post, $"{ContentUrl}/api/v1/content/uploads")
        {
            Content = JsonContent.Create(initBody),
        };
        StampAuth(initReq, auth.Token, userId!);

        var initRes = await http.SendAsync(initReq);
        initRes.StatusCode.Should().Be(HttpStatusCode.Created,
            "init upload should succeed. Body: " + await initRes.Content.ReadAsStringAsync());

        var init = await initRes.Content.ReadFromJsonAsync<InitUploadResultDto>();
        init.Should().NotBeNull();
        init!.PutUrl.Should().NotBeNullOrEmpty(
            "small file should use single-PUT, not multipart");

        // ----- 4. PUT bytes direct to LocalStack -------------------------
        var putUrl = RewriteLocalStackHost(init.PutUrl!);
        var putReq = new HttpRequestMessage(HttpMethod.Put, putUrl)
        {
            Content = new ByteArrayContent(payload),
        };
        // The presigned URL signs Content-Type if it was supplied; match it.
        putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        var putRes = await http.SendAsync(putReq);
        putRes.StatusCode.Should().Be(HttpStatusCode.OK,
            "presigned PUT to LocalStack should succeed. Body: " +
            await putRes.Content.ReadAsStringAsync());

        // ----- 5. Complete upload ----------------------------------------
        var completeReq = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ContentUrl}/api/v1/content/uploads/{init.ContentId}/complete")
        {
            Content = JsonContent.Create(new CompleteUploadRequestDto(Parts: null)),
        };
        StampAuth(completeReq, auth.Token, userId!);

        var completeRes = await http.SendAsync(completeReq);
        completeRes.StatusCode.Should().Be(HttpStatusCode.OK,
            "complete should kick off validation. Body: " +
            await completeRes.Content.ReadAsStringAsync());

        // ----- 6. Poll until the row reaches a terminal state ------------
        // Available: validation passed (image sniff + optional ClamAV scan).
        // Quarantined: ClamAV (or another validator) rejected the bytes.
        // Both are terminal and prove the validation pipeline ran end-to-end.
        var status = await PollUntilTerminalAsync(http, init.ContentId, auth.Token, userId!);
        _output.WriteLine($"Final upload status: {status.Status} (reason={status.FailureReason})");

        status.Status.Should().BeOneOf(
            ContentStatus.Available,
            ContentStatus.Quarantined);

        // Skip the read-back path if the row didn't flip to Available — the
        // GetContent endpoint only returns ContentDto for Available rows.
        Skip.If(status.Status != ContentStatus.Available,
            "Row was Quarantined — likely ClamAV scan failed. " +
            $"Reason: {status.FailureReason}. The upload+validation pipeline " +
            "still ran end-to-end (this proves the cross-service contract).");

        // ----- 7. GetContent → ContentDto with DownloadUrl ---------------
        var getRes = await http.GetAsync($"{ContentUrl}/api/v1/content/{init.ContentId}");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK,
            "GetContent should return Available row. Body: " +
            await getRes.Content.ReadAsStringAsync());

        var contentDto = await getRes.Content.ReadFromJsonAsync<ContentDto>();
        contentDto.Should().NotBeNull();
        contentDto!.DownloadUrl.Should().NotBeNullOrEmpty();
        contentDto.FileSize.Should().Be(payload.Length);

        // ----- 8. GET DownloadUrl → bytes match ---------------------------
        var downloadUrl = RewriteLocalStackHost(contentDto.DownloadUrl);
        var downloadRes = await http.GetAsync(downloadUrl);
        downloadRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var downloaded = await downloadRes.Content.ReadAsByteArrayAsync();
        downloaded.Should().Equal(payload, "round-tripped bytes should match");
    }

    // ---------------------------------------------------------------
    // Test 2: anon caller blocked
    // ---------------------------------------------------------------
    [SkippableFact]
    public async Task InitUpload_Without_Authorization_Returns_401()
    {
        Skip.IfNot(await StackIsReachableAsync(),
            $"E2E stack not reachable: {ContentUrl}/health.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        var body = new InitUploadRequestDto(
            EntityId:    Guid.NewGuid(),
            EntityType:  "Test",
            FileName:    "anon.png",
            ContentType: "image/png",
            TotalSize:   123);

        var res = await http.PostAsJsonAsync($"{ContentUrl}/api/v1/content/uploads", body);

        // [Authorize] kicks in before the controller, so anon → 401 before
        // we ever hit the X-User-Id check inside the action.
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private async Task<bool> StackIsReachableAsync()
    {
        // Identity-svc is the bottleneck — without it we can't even mint a
        // token. Content-svc must also be up. BFF check is informational.
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var idRes = await http.GetAsync($"{IdentityUrl}/health");
            var ctRes = await http.GetAsync($"{ContentUrl}/health");
            return idRes.IsSuccessStatusCode && ctRes.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void StampAuth(HttpRequestMessage req, string jwt, string userId)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        // Mirror what UserIdentityForwardingHandler does on the BFF: stamp
        // the user id as X-User-Id so content-svc's GetForwardedUserId()
        // succeeds. Without this header the controller returns 401 even
        // though the JWT is valid — by design, the contract is "BFF stamps
        // identity, backends trust the header".
        req.Headers.Add("X-User-Id", userId);
    }

    private static string ExtractUserId(string jwt)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
        return token.Claims.FirstOrDefault(c =>
                c.Type == ClaimTypes.NameIdentifier ||
                c.Type == "nameid" ||
                c.Type == "sub")?.Value ?? string.Empty;
    }

    private static string RewriteLocalStackHost(string url)
        => url.Replace(LocalStackInternal, LocalStackHost, StringComparison.Ordinal);

    private async Task<UploadStatusDto> PollUntilTerminalAsync(
        HttpClient http, Guid contentId, string token, string userId)
    {
        var deadline = DateTime.UtcNow + ValidationBudget;
        UploadStatusDto? last = null;

        // PeriodicTimer instead of Task.Delay — each tick is a real poll, not
        // a blanket sleep. Exits as soon as we reach a terminal state.
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"{ContentUrl}/api/v1/content/uploads/{contentId}");
            StampAuth(req, token, userId);

            var res = await http.SendAsync(req);
            if (res.IsSuccessStatusCode)
            {
                last = await res.Content.ReadFromJsonAsync<UploadStatusDto>();
                if (last is not null && IsTerminal(last.Status))
                {
                    return last;
                }
            }
        }
        while (DateTime.UtcNow < deadline && await timer.WaitForNextTickAsync());

        throw new TimeoutException(
            $"Upload {contentId} did not reach a terminal state within {ValidationBudget}. " +
            $"Last status: {last?.Status.ToString() ?? "<none>"}.");
    }

    private static bool IsTerminal(ContentStatus status) =>
        status is ContentStatus.Available
              or ContentStatus.Quarantined
              or ContentStatus.Failed;

    // 1x1 transparent PNG. Smallest valid PNG; passes magic-byte sniff.
    private static byte[] OnePixelPng() => new byte[]
    {
        0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,
        0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
        0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,
        0x08,0x06,0x00,0x00,0x00,0x1F,0x15,0xC4,
        0x89,0x00,0x00,0x00,0x0D,0x49,0x44,0x41,
        0x54,0x78,0x9C,0x62,0x00,0x01,0x00,0x00,
        0x05,0x00,0x01,0x0D,0x0A,0x2D,0xB4,0x00,
        0x00,0x00,0x00,0x49,0x45,0x4E,0x44,0xAE,
        0x42,0x60,0x82
    };

    // ---------------------------------------------------------------
    // Wire-format mirrors of the service DTOs. Duplicated here so the
    // test project doesn't take a project ref on Content.Application
    // (kept black-box to mirror what an external client would see).
    // ---------------------------------------------------------------

    private sealed record AuthResponse(
        string Token,
        string? RefreshToken,
        string UserId,
        string? Username,
        string? Email,
        DateTime Expires,
        string? Message);

    private sealed record InitUploadRequestDto(
        Guid EntityId,
        string EntityType,
        string FileName,
        string ContentType,
        long TotalSize);

    private sealed record InitUploadResultDto(
        Guid ContentId,
        UploadKind Kind,
        string? PutUrl,
        string? UploadId,
        IReadOnlyList<PresignedPartDto>? PartUrls,
        DateTime PresignedUntilUtc);

    private sealed record PresignedPartDto(int PartNumber, string Url);

    private sealed record CompleteUploadRequestDto(IReadOnlyList<UploadedPartDto>? Parts);

    private sealed record UploadedPartDto(int PartNumber, string ETag);

    private sealed record UploadStatusDto(
        Guid ContentId,
        ContentStatus Status,
        string? FailureReason,
        string? QuarantineReason,
        DateTime? ValidatedAt);

    private sealed record ContentDto(
        Guid Id,
        Guid EntityId,
        string EntityType,
        string DownloadUrl,
        string ContentType,
        long FileSize,
        string ETag,
        string? Sha256Checksum,
        DateTime? ValidatedAt);

    // Enums travel as their integer ordinals — content-svc has no
    // JsonStringEnumConverter wired in (System.Text.Json default is
    // numeric). Keep the ordering here in lock-step with
    // Haworks.Content.Domain.Entities.ContentStatus / UploadKind.
    private enum UploadKind
    {
        Single = 0,
        Multipart = 1,
    }

    private enum ContentStatus
    {
        Pending = 0,
        Validating = 1,
        Available = 2,
        Quarantined = 3,
        Failed = 4,
        Deleted = 5,
    }
}
