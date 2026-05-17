using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK002Tests
{
    [Fact]
    public async Task GuidNewGuid_InsidePollyExecuteAsync_Reports()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Polly;
            public class PaymentService
            {
                private readonly IAsyncPolicy _policy = new AsyncPolicy();
                public async Task<string> CreatePayment()
                {
                    return await _policy.ExecuteAsync(async () =>
                    {
                        var key = {|#0:Guid.NewGuid()|}.ToString();
                        return key;
                    });
                }
            }
            """;

        var expected = CSharpAnalyzerVerifier<HWK002_NoGuidNewGuidInPollyRetryAnalyzer>
            .Diagnostic(Diagnostics.NoGuidNewGuidInPollyRetry)
            .WithLocation(0)
            .WithArguments("Guid.NewGuid()");

        await CSharpAnalyzerVerifier<HWK002_NoGuidNewGuidInPollyRetryAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task GuidNewGuid_BeforePollyExecuteAsync_NoDiagnostic()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Polly;
            public class PaymentService
            {
                private readonly IAsyncPolicy _policy = new AsyncPolicy();
                public async Task<string> CreatePayment()
                {
                    var key = Guid.NewGuid().ToString();
                    return await _policy.ExecuteAsync(async () => { return key; });
                }
            }
            """;

        await CSharpAnalyzerVerifier<HWK002_NoGuidNewGuidInPollyRetryAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
    }
}
