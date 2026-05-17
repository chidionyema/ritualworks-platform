using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK023Tests
{
    [Fact]
    public async Task TakeWithoutOrderBy_Reports()
    {
        const string source = """
            using System.Linq;
            public class Svc
            {
                public int[] Get(int[] items) => {|#0:items.Take(10)|}.ToArray();
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK023_NoTakeWithoutOrderByAnalyzer>
            .Diagnostic(Diagnostics.NoTakeWithoutOrderBy).WithLocation(0).WithArguments(".Take()");
        await CSharpAnalyzerVerifier<HWK023_NoTakeWithoutOrderByAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task TakeWithOrderBy_NoDiagnostic()
    {
        const string source = """
            using System.Linq;
            public class Svc
            {
                public int[] Get(int[] items) => items.OrderBy(x => x).Take(10).ToArray();
            }
            """;
        await CSharpAnalyzerVerifier<HWK023_NoTakeWithoutOrderByAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
