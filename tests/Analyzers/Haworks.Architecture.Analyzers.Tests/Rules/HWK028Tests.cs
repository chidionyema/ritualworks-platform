using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK028Tests
{
    [Fact]
    public async Task ReturnTask_InTryCatch_WithoutAwait_Reports()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            public class Svc
            {
                public Task DoWork()
                {
                    try
                    {
                        {|#0:return Task.Run(() => throw new Exception());|}
                    }
                    catch (Exception) { return Task.CompletedTask; }
                }
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK028_NoReturnTaskWithoutAwaitInTryCatchAnalyzer>
            .Diagnostic(Diagnostics.NoReturnTaskWithoutAwaitInTryCatch).WithLocation(0).WithArguments("DoWork");
        await CSharpAnalyzerVerifier<HWK028_NoReturnTaskWithoutAwaitInTryCatchAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task AsyncMethod_WithAwait_NoDiagnostic()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            public class Svc
            {
                public async Task DoWork()
                {
                    try { await Task.Delay(1); }
                    catch (Exception) { }
                }
            }
            """;
        await CSharpAnalyzerVerifier<HWK028_NoReturnTaskWithoutAwaitInTryCatchAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
