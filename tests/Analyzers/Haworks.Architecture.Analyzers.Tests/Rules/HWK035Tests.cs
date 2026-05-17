using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK035Tests
{
    [Fact]
    public async Task HardcodedUSD_InService_Reports()
    {
        const string source = """
            public class PaymentService
            {
                public string GetCurrency() => {|#0:"USD"|};
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK035_NoHardcodedCurrencyAnalyzer>
            .Diagnostic(Diagnostics.NoHardcodedCurrency).WithLocation(0).WithArguments("USD");
        await CSharpAnalyzerVerifier<HWK035_NoHardcodedCurrencyAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task CurrencyFromConfig_NoDiagnostic()
    {
        const string source = """
            public class PaymentService
            {
                private readonly string _currency;
                public PaymentService(string currency) { _currency = currency; }
                public string GetCurrency() => _currency;
            }
            """;
        await CSharpAnalyzerVerifier<HWK035_NoHardcodedCurrencyAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
