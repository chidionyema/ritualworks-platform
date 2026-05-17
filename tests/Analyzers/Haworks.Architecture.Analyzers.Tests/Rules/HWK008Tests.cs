using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK008Tests
{
    [Fact]
    public async Task ExecuteUpdateAsync_InsideConsumer_Reports()
    {
        const string source = """
            using System.Threading.Tasks;
            using MassTransit;
            public record OrderCreatedEvent;
            public static class EfExtensions
            {
                public static Task<int> ExecuteUpdateAsync(this object q) => Task.FromResult(0);
            }
            public class BadConsumer : IConsumer<OrderCreatedEvent>
            {
                public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
                {
                    await {|#0:new object().ExecuteUpdateAsync()|};
                }
            }
            """;

        var expected = CSharpAnalyzerVerifier<HWK008_NoExecuteUpdateInConsumerAnalyzer>
            .Diagnostic(Diagnostics.NoExecuteUpdateInConsumer)
            .WithLocation(0)
            .WithArguments("ExecuteUpdateAsync");

        await CSharpAnalyzerVerifier<HWK008_NoExecuteUpdateInConsumerAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_InsideConsumer_Reports()
    {
        const string source = """
            using System.Threading.Tasks;
            using MassTransit;
            public record OrderCreatedEvent;
            public static class EfExtensions
            {
                public static Task<int> ExecuteDeleteAsync(this object q) => Task.FromResult(0);
            }
            public class BadConsumer : IConsumer<OrderCreatedEvent>
            {
                public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
                {
                    await {|#0:new object().ExecuteDeleteAsync()|};
                }
            }
            """;

        var expected = CSharpAnalyzerVerifier<HWK008_NoExecuteUpdateInConsumerAnalyzer>
            .Diagnostic(Diagnostics.NoExecuteUpdateInConsumer)
            .WithLocation(0)
            .WithArguments("ExecuteDeleteAsync");

        await CSharpAnalyzerVerifier<HWK008_NoExecuteUpdateInConsumerAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }
}
