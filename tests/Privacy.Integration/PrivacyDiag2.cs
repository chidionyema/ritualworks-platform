using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Contracts.Privacy;
using Haworks.Privacy.Application.Requests.Sagas;
using Haworks.Privacy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Privacy.Integration;

public sealed class PrivacyDiag2 : IClassFixture<PrivacyWebAppFactory>, IAsyncLifetime
{
    private readonly PrivacyWebAppFactory _factory;
    public PrivacyDiag2(PrivacyWebAppFactory factory) => _factory = factory;
    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Diag2()
    {
        var bus = _factory.Services.GetRequiredService<IBusControl>();
        await bus.Publish(new InitiatePrivacyRequestMessage(Guid.NewGuid(), Guid.NewGuid()));
        await Task.Delay(10000);
        
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var faults = harness.Published.Select<Fault<InitiatePrivacyRequestMessage>>().ToList();
        foreach (var f in faults)
        {
            void PrintEx(ExceptionInfo ex, int depth)
            {
                var prefix = new string(' ', depth * 2);
                Console.WriteLine($"{prefix}EX[{depth}] Type={ex.ExceptionType}");
                Console.WriteLine($"{prefix}EX[{depth}] Msg={ex.Message}");
                if (ex.StackTrace != null)
                    Console.WriteLine($"{prefix}EX[{depth}] Stack={ex.StackTrace.Substring(0, Math.Min(500, ex.StackTrace.Length))}");
                if (ex.InnerException != null)
                    PrintEx(ex.InnerException, depth + 1);
            }
            foreach (var ex in f.Context.Message.Exceptions)
                PrintEx(ex, 0);
        }
        faults.Should().BeEmpty("saga should not fault");
    }
}
