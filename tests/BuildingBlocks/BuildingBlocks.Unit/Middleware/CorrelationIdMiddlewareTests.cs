using FluentAssertions;
using Haworks.BuildingBlocks.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Middleware;

/// <summary>
/// Exercises the correlation-id middleware via a minimal in-memory pipeline.
/// We avoid <c>TestServer</c> here because a <see cref="DefaultHttpContext"/>
/// + an <see cref="ApplicationBuilder"/> is enough to verify the contract and
/// keeps the unit-test footprint tiny.
/// </summary>
public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task MissingHeader_GeneratesId_StampsResponse_AndSetsItem()
    {
        var ctx = NewContextWithBody();

        await BuildPipeline().Invoke(ctx);
        await FireOnStartingAsync(ctx);

        ctx.Items[CorrelationIdMiddleware.ItemsKey].Should().BeOfType<string>();
        var id = (string)ctx.Items[CorrelationIdMiddleware.ItemsKey]!;
        id.Should().NotBeNullOrWhiteSpace();
        // Guid.NewGuid().ToString("N") = 32 hex chars, no hyphens. Don't pin
        // to that exact format (we may swap to ULID later) — just sanity-check.
        id.Length.Should().BeGreaterThan(8);

        ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString()
            .Should().Be(id);
    }

    [Fact]
    public async Task PresentHeader_PreservesId_ThroughRequestAndResponse()
    {
        const string supplied = "support-ticket-42-abc";
        var ctx = NewContextWithBody();
        ctx.Request.Headers[CorrelationIdMiddleware.HeaderName] = supplied;

        await BuildPipeline().Invoke(ctx);
        await FireOnStartingAsync(ctx);

        ctx.Items[CorrelationIdMiddleware.ItemsKey].Should().Be(supplied);
        ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString()
            .Should().Be(supplied);
    }

    [Fact]
    public async Task LogContextProperty_IsPushed_WhileRequestInFlight()
    {
        // Spy on Serilog to verify the property reaches log lines emitted
        // from inside the middleware scope. This is the headline reason the
        // middleware exists, so we test it explicitly rather than just
        // trusting the LogContext.PushProperty call site.
        var sink = new CapturingSink();
        var prevLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            const string supplied = "fixed-id-for-assertion";
            var ctx = NewContextWithBody();
            ctx.Request.Headers[CorrelationIdMiddleware.HeaderName] = supplied;

            var pipeline = BuildPipeline(terminal: _ =>
            {
                Log.Information("inside-pipeline");
                return Task.CompletedTask;
            });

            await pipeline.Invoke(ctx);

            sink.Events.Should().ContainSingle(e => e.MessageTemplate.Text == "inside-pipeline")
                .Which.Properties.Should().ContainKey(CorrelationIdMiddleware.ItemsKey)
                .WhoseValue.ToString().Should().Be($"\"{supplied}\"");
        }
        finally
        {
            Log.Logger = prevLogger;
        }
    }

    [Fact]
    public async Task IdempotentReentry_DoesNotDoublePushOrRegenerate()
    {
        // Second invocation against the same context should treat the stashed
        // id as authoritative and not mint a new one.
        const string preset = "preset-id";
        var ctx = NewContextWithBody();
        ctx.Items[CorrelationIdMiddleware.ItemsKey] = preset;

        var pipeline = BuildPipeline();
        await pipeline.Invoke(ctx);
        await pipeline.Invoke(ctx);
        await FireOnStartingAsync(ctx);

        ctx.Items[CorrelationIdMiddleware.ItemsKey].Should().Be(preset);
        // Header must only have one value — duplicates would mean the
        // middleware re-stamped on the second pass.
        ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].Count.Should().Be(1);
        ctx.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString()
            .Should().Be(preset);
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>
    /// Build a minimal request pipeline that runs the correlation-id
    /// middleware then a no-op (or caller-supplied) terminal delegate.
    /// </summary>
    private static RequestDelegate BuildPipeline(RequestDelegate? terminal = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseCorrelationId();
        app.Run(terminal ?? (_ => Task.CompletedTask));
        return app.Build();
    }

    /// <summary>
    /// <see cref="DefaultHttpContext"/> with a deterministic
    /// <see cref="IHttpResponseFeature"/> that captures OnStarting callbacks
    /// so the test can fire them after the pipeline returns. The stock
    /// feature keeps callbacks but only invokes them on first body write,
    /// which never happens in these tests (no terminal handler writes).
    /// </summary>
    private static DefaultHttpContext NewContextWithBody()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Features.Set<IHttpResponseFeature>(new CapturingResponseFeature());
        return ctx;
    }

    /// <summary>
    /// Manually fires the captured OnStarting callbacks, simulating the
    /// runtime behaviour just before the response is flushed.
    /// </summary>
    private static async Task FireOnStartingAsync(HttpContext ctx)
    {
        var feat = ctx.Features.Get<IHttpResponseFeature>();
        if (feat is CapturingResponseFeature cap)
        {
            await cap.FireOnStartingAsync();
        }
    }

    private sealed class CapturingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> cb, object state)> _callbacks = new();

        public override void OnStarting(Func<object, Task> callback, object state)
            => _callbacks.Add((callback, state));

        public async Task FireOnStartingAsync()
        {
            // Reverse order matches Kestrel: last-registered runs first so
            // outer middleware can observe inner-set state.
            for (var i = _callbacks.Count - 1; i >= 0; i--)
            {
                await _callbacks[i].cb(_callbacks[i].state);
            }
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
