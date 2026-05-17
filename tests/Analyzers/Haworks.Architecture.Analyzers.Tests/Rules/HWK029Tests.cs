using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK029Tests
{
    [Fact]
    public async Task DoubleAmount_Reports()
    {
        const string source = """
            public class Payment { public double {|#0:Amount|} { get; set; } }
            """;
        var expected = CSharpAnalyzerVerifier<HWK029_NoFloatForFinancialAnalyzer>
            .Diagnostic(Diagnostics.NoFloatForFinancial).WithLocation(0).WithArguments("Amount");
        await CSharpAnalyzerVerifier<HWK029_NoFloatForFinancialAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task DecimalAmount_NoDiagnostic()
    {
        const string source = """
            public class Payment { public decimal Amount { get; set; } }
            """;
        await CSharpAnalyzerVerifier<HWK029_NoFloatForFinancialAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }

    [Fact]
    public async Task DoubleLatitude_NoDiagnostic()
    {
        const string source = """
            public class Location { public double Latitude { get; set; } }
            """;
        await CSharpAnalyzerVerifier<HWK029_NoFloatForFinancialAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
