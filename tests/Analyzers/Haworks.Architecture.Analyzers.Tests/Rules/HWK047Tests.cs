using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK047Tests
{
    [Fact]
    public async Task PublicSetterId_InDomainEntity_Reports()
    {
        const string source = """
            namespace MyApp.Domain
            {
                public class Order { public System.Guid {|#0:Id|} { get; set; } }
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK047_NoPublicSetterOnEntityIdAnalyzer>
            .Diagnostic(Diagnostics.NoPublicSetterOnEntityId).WithLocation(0).WithArguments("Id");
        await CSharpAnalyzerVerifier<HWK047_NoPublicSetterOnEntityIdAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task PrivateSetterId_NoDiagnostic()
    {
        const string source = """
            namespace MyApp.Domain
            {
                public class Order { public System.Guid Id { get; private set; } }
            }
            """;
        await CSharpAnalyzerVerifier<HWK047_NoPublicSetterOnEntityIdAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }

    [Fact]
    public async Task InitOnlyId_NoDiagnostic()
    {
        const string source = """
            namespace MyApp.Domain
            {
                public class Order { public System.Guid Id { get; init; } }
            }
            """;
        await CSharpAnalyzerVerifier<HWK047_NoPublicSetterOnEntityIdAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
