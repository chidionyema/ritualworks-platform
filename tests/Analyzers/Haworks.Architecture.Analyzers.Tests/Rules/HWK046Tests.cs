using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK046Tests
{
    [Fact]
    public async Task InterpolatedString_InLogger_Reports()
    {
        const string source = """
            public class Logger { public void LogInformation(string msg) {} }
            public class Svc
            {
                private readonly Logger _logger = new();
                public void Do(string name) { _logger.LogInformation({|#0:$"Hello {name}"|}); }
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK046_NoStringInterpolationInLoggerAnalyzer>
            .Diagnostic(Diagnostics.NoStringInterpolationInLogger).WithLocation(0);
        await CSharpAnalyzerVerifier<HWK046_NoStringInterpolationInLoggerAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task MessageTemplate_InLogger_NoDiagnostic()
    {
        const string source = """
            public class Logger { public void LogInformation(string msg, params object[] args) {} }
            public class Svc
            {
                private readonly Logger _logger = new();
                public void Do(string name) { _logger.LogInformation("Hello {Name}", name); }
            }
            """;
        await CSharpAnalyzerVerifier<HWK046_NoStringInterpolationInLoggerAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
