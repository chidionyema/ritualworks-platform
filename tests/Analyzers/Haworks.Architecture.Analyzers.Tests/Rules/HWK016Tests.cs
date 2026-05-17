using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK016Tests
{
    [Fact]
    public async Task PositionalRecord_WithEventSuffix_Reports()
    {
        const string source = """
            public record {|#0:OrderCreatedEvent|}(System.Guid OrderId, string Email);
            """;

        var expected = CSharpAnalyzerVerifier<HWK016_NoPositionalRecordForEventsAnalyzer>
            .Diagnostic(Diagnostics.NoPositionalRecordForEvents)
            .WithLocation(0)
            .WithArguments("OrderCreatedEvent");

        await CSharpAnalyzerVerifier<HWK016_NoPositionalRecordForEventsAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task NonPositionalRecord_WithEventSuffix_NoDiagnostic()
    {
        const string source = """
            public record OrderCreatedEvent
            {
                public System.Guid OrderId { get; init; }
                public string Email { get; init; } = "";
            }
            """;

        await CSharpAnalyzerVerifier<HWK016_NoPositionalRecordForEventsAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
    }

    [Fact]
    public async Task PositionalRecord_WithoutEventSuffix_NoDiagnostic()
    {
        const string source = """
            public record Point(double X, double Y);
            """;

        await CSharpAnalyzerVerifier<HWK016_NoPositionalRecordForEventsAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
    }
}
