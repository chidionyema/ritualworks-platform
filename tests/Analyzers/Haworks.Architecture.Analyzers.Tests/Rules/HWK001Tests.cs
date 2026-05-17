using Haworks.Architecture.Analyzers.Rules;
using Haworks.Architecture.Analyzers.Tests.Verifiers;
using Xunit;

namespace Haworks.Architecture.Analyzers.Tests.Rules;

public class HWK001Tests
{
    [Fact]
    public async Task SaveChangesAsync_InsideConsumer_Reports()
    {
        const string source = """
            using System.Threading.Tasks;
            using MassTransit;
            using Microsoft.EntityFrameworkCore;
            public record OrderCreatedEvent;
            public class MyDb : DbContext { }
            public class OrderConsumer : IConsumer<OrderCreatedEvent>
            {
                private readonly MyDb _db = new();
                public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
                {
                    await {|#0:_db.SaveChangesAsync(context.CancellationToken)|};
                }
            }
            """;

        var expected = CSharpAnalyzerVerifier<HWK001_NoManualSaveChangesInConsumerAnalyzer>
            .Diagnostic(Diagnostics.NoManualSaveChangesInConsumer)
            .WithLocation(0)
            .WithArguments("SaveChangesAsync");

        await CSharpAnalyzerVerifier<HWK001_NoManualSaveChangesInConsumerAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task SaveChangesAsync_OutsideConsumer_NoDiagnostic()
    {
        const string source = """
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            public class MyDb : DbContext { }
            public class OrderService
            {
                private readonly MyDb _db = new();
                public async Task DoWork() { await _db.SaveChangesAsync(); }
            }
            """;

        await CSharpAnalyzerVerifier<HWK001_NoManualSaveChangesInConsumerAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
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

        await CSharpAnalyzerVerifier<HWK001_NoManualSaveChangesInConsumerAnalyzer>
            .VerifyNoDiagnosticsAsync(source);
    }
}
