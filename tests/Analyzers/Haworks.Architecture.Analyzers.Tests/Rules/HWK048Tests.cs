using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK048Tests
{
    [Fact]
    public async Task Entity_WithOnlyParameterizedCtor_Reports()
    {
        const string source = """
            namespace MyApp.Domain
            {
                public class {|#0:Order|}
                {
                    public System.Guid Id { get; private set; }
                    public Order(System.Guid id) { Id = id; }
                }
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK048_EntityMustHaveParameterlessConstructorAnalyzer>
            .Diagnostic(Diagnostics.EntityMustHaveParameterlessConstructor).WithLocation(0).WithArguments("Order");
        await CSharpAnalyzerVerifier<HWK048_EntityMustHaveParameterlessConstructorAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task Entity_WithParameterlessCtor_NoDiagnostic()
    {
        const string source = """
            namespace MyApp.Domain
            {
                public class Order
                {
                    public System.Guid Id { get; private set; }
                    public Order(System.Guid id) { Id = id; }
                    private Order() { }
                }
            }
            """;
        await CSharpAnalyzerVerifier<HWK048_EntityMustHaveParameterlessConstructorAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
