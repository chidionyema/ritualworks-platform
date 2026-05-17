using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK036Tests
{
    [Fact]
    public async Task ExecuteAsync_WithoutTryCatch_Reports()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public abstract class BackgroundService
            {
                protected abstract Task ExecuteAsync(CancellationToken ct);
            }
            public class MyWorker : BackgroundService
            {
                protected override async Task {|#0:ExecuteAsync|}(CancellationToken ct)
                {
                    await Task.Delay(1, ct);
                }
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK036_BackgroundServiceMustTryCatchAnalyzer>
            .Diagnostic(Diagnostics.BackgroundServiceMustTryCatch).WithLocation(0).WithArguments("MyWorker");
        await CSharpAnalyzerVerifier<HWK036_BackgroundServiceMustTryCatchAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task ExecuteAsync_WithTryCatch_NoDiagnostic()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            public abstract class BackgroundService
            {
                protected abstract Task ExecuteAsync(CancellationToken ct);
            }
            public class MyWorker : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken ct)
                {
                    try { await Task.Delay(1, ct); }
                    catch (Exception) { }
                }
            }
            """;
        await CSharpAnalyzerVerifier<HWK036_BackgroundServiceMustTryCatchAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
