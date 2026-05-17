using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK021Tests
{
    [Fact]
    public async Task TaskResult_Reports()
    {
        const string source = """
            using System.Threading.Tasks;
            public class Svc
            {
                public string Get()
                {
                    var t = Task.FromResult("x");
                    return t.{|#0:Result|};
                }
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK021_NoTaskResultOrWaitAnalyzer>
            .Diagnostic(Diagnostics.NoTaskResultOrWait).WithLocation(0).WithArguments(".Result");
        await CSharpAnalyzerVerifier<HWK021_NoTaskResultOrWaitAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task AwaitTask_NoDiagnostic()
    {
        const string source = """
            using System.Threading.Tasks;
            public class Svc { public async Task<string> Get() => await Task.FromResult("x"); }
            """;
        await CSharpAnalyzerVerifier<HWK021_NoTaskResultOrWaitAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
