using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Haworks.Architecture.Analyzers.Tests.Verifiers;

public static class CSharpAnalyzerVerifier<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    public static DiagnosticResult Diagnostic(DiagnosticDescriptor descriptor) =>
        CSharpAnalyzerVerifier<TAnalyzer, DefaultVerifier>.Diagnostic(descriptor);

    public static async Task VerifyAnalyzerAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new Test { TestCode = source };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync(CancellationToken.None);
    }

    public static async Task VerifyNoDiagnosticsAsync(string source)
    {
        var test = new Test { TestCode = source };
        await test.RunAsync(CancellationToken.None);
    }

    private sealed class Test : CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
    {
        public Test()
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
            TestState.Sources.Add(Stubs.MassTransit);
            TestState.Sources.Add(Stubs.EfCore);
            TestState.Sources.Add(Stubs.Polly);
        }
    }
}

internal static class Stubs
{
    public const string MassTransit = """
        namespace MassTransit
        {
            public interface IConsumer<T> where T : class
            {
                System.Threading.Tasks.Task Consume(ConsumeContext<T> context);
            }
            public interface ConsumeContext<out T> where T : class
            {
                T Message { get; }
                System.Threading.CancellationToken CancellationToken { get; }
            }
        }
        """;

    public const string EfCore = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext
            {
                public virtual System.Threading.Tasks.Task<int> SaveChangesAsync(System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(0);
                public Infrastructure.DatabaseFacade Database => new Infrastructure.DatabaseFacade();
            }
            namespace Infrastructure
            {
                public class DatabaseFacade
                {
                    public System.Threading.Tasks.Task<System.IDisposable> BeginTransactionAsync(System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult<System.IDisposable>(null!);
                }
            }
            namespace Metadata.Builders
            {
                public class EntityTypeBuilder<T> where T : class
                {
                    public PropertyBuilder<TProp> Property<TProp>(System.Linq.Expressions.Expression<System.Func<T, TProp>> expr) => new PropertyBuilder<TProp>();
                }
                public class PropertyBuilder<T>
                {
                    public PropertyBuilder<T> HasColumnType(string typeName) => this;
                    public PropertyBuilder<T> HasPrecision(int precision, int scale) => this;
                    public PropertyBuilder<T> IsRequired() => this;
                }
            }
        }
        """;

    public const string Polly = """
        namespace Polly
        {
            public class AsyncPolicy : IAsyncPolicy
            {
                public System.Threading.Tasks.Task<TResult> ExecuteAsync<TResult>(System.Func<System.Threading.Tasks.Task<TResult>> action) => action();
                public System.Threading.Tasks.Task<TResult> ExecuteAsync<TResult>(System.Func<Context, System.Threading.CancellationToken, System.Threading.Tasks.Task<TResult>> action, Context context, System.Threading.CancellationToken ct) => action(context, ct);
            }
            public interface IAsyncPolicy
            {
                System.Threading.Tasks.Task<TResult> ExecuteAsync<TResult>(System.Func<System.Threading.Tasks.Task<TResult>> action);
                System.Threading.Tasks.Task<TResult> ExecuteAsync<TResult>(System.Func<Context, System.Threading.CancellationToken, System.Threading.Tasks.Task<TResult>> action, Context context, System.Threading.CancellationToken ct);
            }
            public class Context { }
        }
        """;
}
