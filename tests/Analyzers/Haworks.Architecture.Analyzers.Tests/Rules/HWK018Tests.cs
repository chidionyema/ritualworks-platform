using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK018Tests
{
    [Fact]
    public async Task ThrowNotImplementedException_Reports()
    {
        const string source = """
            using System;
            public class Service
            {
                public void DoWork() { {|#0:throw new NotImplementedException();|}  }
            }
            """;

        var expected = CSharpAnalyzerVerifier<HWK018_NoNotImplementedExceptionAnalyzer>
            .Diagnostic(Diagnostics.NoNotImplementedException)
            .WithLocation(0)
            .WithArguments("NotImplementedException");

        await CSharpAnalyzerVerifier<HWK018_NoNotImplementedExceptionAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task ThrowArgumentException_NoDiagnostic()
    {
        const string source = """
            using System;
            public class Service
            {
                public void DoWork(string x) { throw new ArgumentException("bad", nameof(x)); }
            }
            """;

        await CSharpAnalyzerVerifier<HWK018_NoNotImplementedExceptionAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
    }
}
