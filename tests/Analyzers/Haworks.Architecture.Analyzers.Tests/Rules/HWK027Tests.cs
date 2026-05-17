using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK027Tests
{
    [Fact]
    public async Task InterpolatedString_InFromSqlRaw_Reports()
    {
        const string source = """
            public static class DbExt
            {
                public static object FromSqlRaw(this object db, string sql) => db;
            }
            public class Repo
            {
                private readonly object _db = new();
                public object Query(string userId) => _db.FromSqlRaw({|#0:$"SELECT * FROM Users WHERE Id = '{userId}'"|});
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK027_NoStringConcatInSqlAnalyzer>
            .Diagnostic(Diagnostics.NoStringConcatInSql).WithLocation(0).WithArguments("FromSqlRaw");
        await CSharpAnalyzerVerifier<HWK027_NoStringConcatInSqlAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task ParameterizedQuery_NoDiagnostic()
    {
        const string source = """
            public static class DbExt
            {
                public static object FromSqlRaw(this object db, string sql, params object[] p) => db;
            }
            public class Repo
            {
                private readonly object _db = new();
                public object Query(string userId) => _db.FromSqlRaw("SELECT * FROM Users WHERE Id = {0}", userId);
            }
            """;
        await CSharpAnalyzerVerifier<HWK027_NoStringConcatInSqlAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
