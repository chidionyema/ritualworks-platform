using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK022Tests
{
    [Fact]
    public async Task NewHttpClient_Reports()
    {
        const string source = """
            using System.Net.Http;
            public class Svc { public HttpClient Get() => {|#0:new HttpClient()|}; }
            """;
        var expected = CSharpAnalyzerVerifier<HWK022_NoNewHttpClientAnalyzer>
            .Diagnostic(Diagnostics.NoNewHttpClient).WithLocation(0).WithArguments("new HttpClient()");
        await CSharpAnalyzerVerifier<HWK022_NoNewHttpClientAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task NewHttpClient_WithHandler_NoDiagnostic()
    {
        const string source = """
            using System.Net.Http;
            public class Svc
            {
                public HttpClient Get(HttpMessageHandler h) => new HttpClient(h);
            }
            """;
        await CSharpAnalyzerVerifier<HWK022_NoNewHttpClientAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
