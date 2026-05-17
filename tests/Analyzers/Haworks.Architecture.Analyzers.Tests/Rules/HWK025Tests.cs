using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK025Tests
{
    [Fact]
    public async Task CatchAll_NoLogNoThrow_Reports()
    {
        const string source = """
            using System;
            public class Svc
            {
                public void Do()
                {
                    try { int.Parse("x"); }
                    {|#0:catch|} (Exception) { var x = 1; }
                }
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK025_NoCatchAllWithoutLoggingAnalyzer>
            .Diagnostic(Diagnostics.NoCatchAllWithoutLogging).WithLocation(0).WithArguments("Exception");
        await CSharpAnalyzerVerifier<HWK025_NoCatchAllWithoutLoggingAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task CatchAll_WithThrow_NoDiagnostic()
    {
        const string source = """
            using System;
            public class Svc
            {
                public void Do()
                {
                    try { int.Parse("x"); }
                    catch (Exception) { throw; }
                }
            }
            """;
        await CSharpAnalyzerVerifier<HWK025_NoCatchAllWithoutLoggingAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }

    [Fact]
    public async Task CatchAll_WithLogging_NoDiagnostic()
    {
        const string source = """
            using System;
            public class Svc
            {
                private readonly object _logger = null!;
                public void Do()
                {
                    try { int.Parse("x"); }
                    catch (Exception ex) { _logger.ToString(); }
                }
            }
            """;
        await CSharpAnalyzerVerifier<HWK025_NoCatchAllWithoutLoggingAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
