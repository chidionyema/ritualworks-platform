using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK024Tests
{
    [Fact]
    public async Task ThreadSleep_Reports()
    {
        const string source = """
            using System.Threading;
            public class Svc { public void Wait() { {|#0:Thread.Sleep(1000)|}; } }
            """;
        var expected = CSharpAnalyzerVerifier<HWK024_NoThreadSleepAnalyzer>
            .Diagnostic(Diagnostics.NoThreadSleep).WithLocation(0).WithArguments("Thread.Sleep()");
        await CSharpAnalyzerVerifier<HWK024_NoThreadSleepAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task TaskDelay_NoDiagnostic()
    {
        const string source = """
            using System.Threading.Tasks;
            public class Svc { public async Task Wait() { await Task.Delay(1000); } }
            """;
        await CSharpAnalyzerVerifier<HWK024_NoThreadSleepAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
