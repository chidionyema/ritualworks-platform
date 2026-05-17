using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK032Tests
{
    [Fact]
    public async Task AsNoTracking_WithSaveChanges_Reports()
    {
        const string source = """
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            public class MyDb : DbContext { }
            public class Repo
            {
                private readonly MyDb _db = new();
                public async Task {|#0:Bad|}()
                {
                    var x = _db.Database.ToString();
                    _db.AsNoTracking();
                    await _db.SaveChangesAsync();
                }
            }
            public static class Ext
            {
                public static object AsNoTracking(this object o) => o;
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK032_NoAsNoTrackingWithSaveChangesAnalyzer>
            .Diagnostic(Diagnostics.NoAsNoTrackingWithSaveChanges).WithLocation(0).WithArguments("Bad");
        await CSharpAnalyzerVerifier<HWK032_NoAsNoTrackingWithSaveChangesAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task AsNoTracking_WithoutSaveChanges_NoDiagnostic()
    {
        const string source = """
            public static class Ext { public static object AsNoTracking(this object o) => o; }
            public class Repo
            {
                public object Query() => new object().AsNoTracking();
            }
            """;
        await CSharpAnalyzerVerifier<HWK032_NoAsNoTrackingWithSaveChangesAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
