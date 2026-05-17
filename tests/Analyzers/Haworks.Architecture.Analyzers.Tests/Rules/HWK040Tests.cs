using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK040Tests
{
    [Fact]
    public async Task AsyncMethod_WithoutCancellationToken_Reports()
    {
        const string source = """
            using System.Threading.Tasks;
            public class Service
            {
                public async Task {|#0:DoWork|}() { await Task.Delay(1); }
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK040_AsyncMethodMustAcceptCancellationTokenAnalyzer>
            .Diagnostic(Diagnostics.AsyncMethodMustAcceptCancellationToken).WithLocation(0).WithArguments("DoWork");
        await CSharpAnalyzerVerifier<HWK040_AsyncMethodMustAcceptCancellationTokenAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task AsyncMethod_WithCancellationToken_NoDiagnostic()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class Service
            {
                public async Task DoWork(CancellationToken ct) { await Task.Delay(1, ct); }
            }
            """;
        await CSharpAnalyzerVerifier<HWK040_AsyncMethodMustAcceptCancellationTokenAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
