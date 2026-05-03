namespace Haworks.Identity.UnitTests.Helpers;

public static class UnitTestConstants
{
    public static class Auth
    {
        public const string ValidPassword = "ValidPassword123!";
        public const string WeakPassword = "weak";
        public const string InvalidPassword = "WrongPassword!";
        public const string ValidJwtKey = "VGhpc0lzQVZlcnlTZWN1cmVLZXlGb3JUZXN0aW5nMTIz";
        public const string TestIssuer = "test-issuer";
        public const string TestAudience = "test-audience";
        public static readonly TimeSpan TokenExpiration = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan RefreshTokenExpiration = TimeSpan.FromDays(7);
        public static readonly TimeSpan TimingTolerance = TimeSpan.FromSeconds(5);
    }

    public static class Users
    {
        public const string DefaultUsername = "testuser";
        public const string DefaultEmail = "test@example.com";
        public const string NonExistentUsername = "nonexistent";
        public const string DefaultUserId = "user-123";
    }
}
