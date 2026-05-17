using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK007Tests
{
    [Fact]
    public async Task IPublishEndpoint_InsideConsumer_Reports()
    {
        const string source = """
            using System.Threading.Tasks;
            using MassTransit;
            public record OrderCreatedEvent;
            public record NotifyEvent;
            public interface IPublishEndpoint
            {
                Task Publish<T>(T message, System.Threading.CancellationToken ct = default) where T : class;
            }
            public class BadConsumer : IConsumer<OrderCreatedEvent>
            {
                private readonly IPublishEndpoint _pub = null!;
                public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
                {
                    await {|#0:_pub.Publish(new NotifyEvent())|};
                }
            }
            """;

        var expected = CSharpAnalyzerVerifier<HWK007_MustUseContextPublishInConsumerAnalyzer>
            .Diagnostic(Diagnostics.MustUseContextPublishInConsumer)
            .WithLocation(0)
            .WithArguments("IPublishEndpoint.Publish");

        await CSharpAnalyzerVerifier<HWK007_MustUseContextPublishInConsumerAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task ContextPublish_InsideConsumer_IsCorrectPattern()
    {
        const string source = """
            using System.Threading.Tasks;
            using MassTransit;
            public record OrderCreatedEvent;
            public record NotifyEvent;
            public class GoodConsumer : IConsumer<OrderCreatedEvent>
            {
                public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
                {
                    await context.Publish(new NotifyEvent());
                }
            }
            """;

        await CSharpAnalyzerVerifier<HWK007_MustUseContextPublishInConsumerAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
    }
}
