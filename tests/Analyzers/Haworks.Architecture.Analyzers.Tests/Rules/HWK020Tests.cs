using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK020Tests
{
    [Fact]
    public async Task DateTimeNow_Reports()
    {
        const string source = """
            using System;
            public class Svc { public DateTime Get() => {|#0:DateTime.Now|}; }
            """;
        var expected = CSharpAnalyzerVerifier<HWK020_NoDateTimeNowAnalyzer>
            .Diagnostic(Diagnostics.NoDateTimeNow).WithLocation(0).WithArguments("DateTime.Now");
        await CSharpAnalyzerVerifier<HWK020_NoDateTimeNowAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task DateTimeUtcNow_NoDiagnostic()
    {
        const string source = """
            using System;
            public class Svc { public DateTime Get() => DateTime.UtcNow; }
            """;
        await CSharpAnalyzerVerifier<HWK020_NoDateTimeNowAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
