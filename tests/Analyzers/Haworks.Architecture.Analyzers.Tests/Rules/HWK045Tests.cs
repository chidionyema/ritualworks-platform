using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK045Tests
{
    [Fact]
    public async Task LoggingPassword_Reports()
    {
        const string source = """
            public class Logger { public void LogInformation(string msg, params object[] args) {} }
            public class Svc
            {
                private readonly Logger _logger = new();
                public void Do(string password) { _logger.LogInformation("User login", {|#0:password|}); }
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK045_NoSecretsInLogsAnalyzer>
            .Diagnostic(Diagnostics.NoSecretsInLogs).WithLocation(0).WithArguments("password");
        await CSharpAnalyzerVerifier<HWK045_NoSecretsInLogsAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task LoggingUserId_NoDiagnostic()
    {
        const string source = """
            public class Logger { public void LogInformation(string msg, params object[] args) {} }
            public class Svc
            {
                private readonly Logger _logger = new();
                public void Do(string userId) { _logger.LogInformation("User {UserId}", userId); }
            }
            """;
        await CSharpAnalyzerVerifier<HWK045_NoSecretsInLogsAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
