using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK031EventTests
{
    [Fact]
    public async Task MutableList_InEvent_Reports()
    {
        const string source = """
            using System.Collections.Generic;
            public record OrderCreatedEvent
            {
                public System.Guid OrderId { get; init; }
                public List<string> {|#0:Items|} { get; init; } = new();
            }
            """;
        var expected = CSharpAnalyzerVerifier<HWK031_EventsMustUseImmutableCollectionsAnalyzer>
            .Diagnostic(Diagnostics.EventsMustUseImmutableCollections).WithLocation(0).WithArguments("Items", "List<string>");
        await CSharpAnalyzerVerifier<HWK031_EventsMustUseImmutableCollectionsAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task IReadOnlyList_InEvent_NoDiagnostic()
    {
        const string source = """
            using System.Collections.Generic;
            public record OrderCreatedEvent
            {
                public System.Guid OrderId { get; init; }
                public IReadOnlyList<string> Items { get; init; } = System.Array.Empty<string>();
            }
            """;
        await CSharpAnalyzerVerifier<HWK031_EventsMustUseImmutableCollectionsAnalyzer>.VerifyNoDiagnosticsAsync(source);
    }
}
