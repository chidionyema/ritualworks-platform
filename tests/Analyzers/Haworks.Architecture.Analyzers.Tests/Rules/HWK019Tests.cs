using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK019Tests
{
    [Fact]
    public async Task HardcodedLocalhost_InUrl_Reports()
    {
        const string source = """
            public class Config
            {
                public string Url = {|#0:"http://localhost:5000/api"|};
            }
            """;

        var expected = CSharpAnalyzerVerifier<HWK019_NoHardcodedLocalhostAnalyzer>
            .Diagnostic(Diagnostics.NoHardcodedLocalhost)
            .WithLocation(0)
            .WithArguments("localhost");

        await CSharpAnalyzerVerifier<HWK019_NoHardcodedLocalhostAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task ConfigurationBased_Url_NoDiagnostic()
    {
        const string source = """
            public class Config
            {
                public string Url = "https://api.haworks.com/v1";
            }
            """;

        await CSharpAnalyzerVerifier<HWK019_NoHardcodedLocalhostAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
    }
}
