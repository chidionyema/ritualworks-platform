using System.Text.Json;
using Haworks.BffWeb.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.BffWeb.Api.Controllers;

/// <summary>
/// Health, metrics, and trace lookup endpoints used by the portfolio site's
/// dashboard panels (TopologyMap, GrafanaPanel, TraceViewer).
///
/// Phase 1: snapshots are static-ish (small randomization on the SSE stream
/// to keep the panels animating). Phase 2 plug-in points: query the Aspire
/// DCP API for real per-service health + read OpenTelemetry's in-memory
/// exporter for genuine spans.
/// </summary>
[ApiController]
[Route("api")]
[AllowAnonymous]
public class SystemController : ControllerBase
{
    private static readonly string[] s_services =
    [
        "API", "POSTGRES", "REDIS", "RABBITMQ", "VAULT",
    ];

    private readonly IDemoTraceStore _traceStore;
    private readonly ILogger<SystemController> _logger;

    public SystemController(IDemoTraceStore traceStore, ILogger<SystemController> logger)
    {
        _traceStore = traceStore;
        _logger = logger;
    }

    [HttpGet("health/snapshot")]
    public IActionResult GetHealthSnapshot() =>
        Ok(new
        {
            services = new[]
            {
                new { id = "api", name = "API", status = "online", latencyMs = 12 },
                new { id = "db", name = "POSTGRES", status = "online", latencyMs = 5 },
                new { id = "redis", name = "REDIS", status = "online", latencyMs = 2 },
                new { id = "mq", name = "RABBITMQ", status = "online", latencyMs = 18 },
                new { id = "vault", name = "VAULT", status = "online", latencyMs = 10 },
            },
            systemStatus = "healthy",
            p99LatencyMs = 42.4,
            availability = 99.998,
            timestamp = DateTime.UtcNow,
        });

    [HttpGet("health/stream")]
    public async Task GetHealthStream(CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        while (!ct.IsCancellationRequested)
        {
            var snapshot = new
            {
                services = new[]
                {
                    new { id = "api", name = "API", status = "online", latencyMs = Random.Shared.Next(10, 20) },
                    new { id = "db", name = "POSTGRES", status = "online", latencyMs = Random.Shared.Next(4, 8) },
                    new { id = "redis", name = "REDIS", status = "online", latencyMs = Random.Shared.Next(1, 3) },
                    new { id = "mq", name = "RABBITMQ", status = "online", latencyMs = Random.Shared.Next(15, 25) },
                    new { id = "vault", name = "VAULT", status = "online", latencyMs = Random.Shared.Next(8, 12) },
                },
                systemStatus = "healthy",
                p99LatencyMs = 42.0 + Random.Shared.NextDouble(),
                availability = 99.998,
                timestamp = DateTime.UtcNow,
            };

            var json = JsonSerializer.Serialize(snapshot);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);

            await Task.Delay(5000, ct);
        }
    }

    [HttpGet("metrics/snapshot")]
    public IActionResult GetMetricsSnapshot() =>
        Ok(new
        {
            ingressEvents24h = 18234,
            clusterAvailability = 99.998,
            p99LatencyMs = 42.4,
            activeSessions = 3,
            timestamp = DateTime.UtcNow,
        });

    [HttpGet("metrics/stream")]
    public async Task GetMetricsStream(CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var basis = 18234;
        while (!ct.IsCancellationRequested)
        {
            basis += Random.Shared.Next(1, 5);
            var snapshot = new
            {
                ingressEvents24h = basis,
                clusterAvailability = 99.998,
                p99LatencyMs = 42.0 + Random.Shared.NextDouble(),
                activeSessions = Random.Shared.Next(2, 6),
                timestamp = DateTime.UtcNow,
            };
            var json = JsonSerializer.Serialize(snapshot);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
            await Task.Delay(5000, ct);
        }
    }

    [HttpGet("traces/{traceId}")]
    public IActionResult GetTrace(string traceId)
    {
        var trace = _traceStore.Get(traceId);
        if (trace is null) return NotFound();

        return Ok(new
        {
            traceId = trace.TraceId,
            rootSpanId = trace.RootSpanId,
            durationMs = trace.DurationMs,
            spans = trace.Spans.Select(s => new
            {
                spanId = s.SpanId,
                parentSpanId = s.ParentSpanId,
                service = s.Service,
                operation = s.Operation,
                startMs = s.StartMs,
                durationMs = s.DurationMs,
                status = s.Status,
                attributes = s.Attributes,
            }),
        });
    }
}
