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
    public async Task BeginTransactionAsync_InsideConsumer_Reports()
    {
        const string source = """
            using System.Threading.Tasks;
            using MassTransit;
            using Microsoft.EntityFrameworkCore;
            public record OrderCreatedEvent;
            public class MyDb : DbContext { }
            public class BadConsumer : IConsumer<OrderCreatedEvent>
            {
                private readonly MyDb _db = new();
                public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
                {
                    await {|#0:_db.Database.BeginTransactionAsync(context.CancellationToken)|};
                }
            }
            """;

        var expected = CSharpAnalyzerVerifier<HWK001_NoManualSaveChangesInConsumerAnalyzer>
            .Diagnostic(Diagnostics.NoBeginTransactionInConsumer)
            .WithLocation(0)
            .WithArguments("BeginTransactionAsync");

        await CSharpAnalyzerVerifier<HWK001_NoManualSaveChangesInConsumerAnalyzer>
            .VerifyAnalyzerAsync(source, expected);
    }
}
