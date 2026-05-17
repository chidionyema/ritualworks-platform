using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK015Tests
{
    [Fact]
    public async Task AsyncVoid_Method_Reports()
    {
        const string source = """
            using System.Threading.Tasks;
            public class Service
            {
                public async void {|#0:FireAndForget|}() { await Task.Delay(1); }
            }
            """;

        var expected = CSharpAnalyzerVerifier<HWK015_NoAsyncVoidAnalyzer>
            .Diagnostic(Diagnostics.NoAsyncVoid)
            .WithLocation(0)
            .WithArguments("FireAndForget");

        await CSharpAnalyzerVerifier<HWK015_NoAsyncVoidAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task AsyncTask_Method_NoDiagnostic()
    {
        const string source = """
            using System.Threading.Tasks;
            public class Service
            {
                public async Task DoWork() { await Task.Delay(1); }
            }
            """;

        await CSharpAnalyzerVerifier<HWK015_NoAsyncVoidAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
    }
}
