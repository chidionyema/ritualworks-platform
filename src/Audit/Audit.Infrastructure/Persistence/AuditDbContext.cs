using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Haworks.Audit.Domain;

namespace Haworks.Audit.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the audit-svc Postgres database.
///
/// L0 declares the DbSets — both <c>AuditEvents</c> (the partitioned
/// append-only event log, populated by L1.B) and <c>AuditExportJobs</c>
/// (export-job tracking, populated by L1.D). Declaring both here keeps
/// the DbContext immutable across phases — L1.B and L1.D each add their
/// own migrations against this same context but never modify the context
/// class itself.
/// </summary>
public sealed class AuditDbContext : DbContext
{
    public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AuditExportJob> AuditExportJobs => Set<AuditExportJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("audit");

        modelBuilder.Entity<AuditEvent>(b =>
        {
            // Composite key — partitioning by occurred_at requires it in the PK.
            // L1.B's migration replaces this with raw partitioned-table SQL;
            // the EF mapping here exists so the DbContext compiles + EF can
            // query the table after L1.B runs.
            b.ToTable("audit_events");
            b.HasKey(e => new { e.Id, e.OccurredAt });
            b.Property(e => e.Id).HasColumnName("id");
            b.Property(e => e.OccurredAt).HasColumnName("occurred_at");
            b.Property(e => e.ReceivedAt).HasColumnName("received_at");
            b.Property(e => e.EventType).HasColumnName("event_type");
            b.Property(e => e.EntityType).HasColumnName("entity_type");
            b.Property(e => e.EntityId).HasColumnName("entity_id");
            b.Property(e => e.ActorId).HasColumnName("actor_id");
            b.Property(e => e.ActorType).HasColumnName("actor_type");
            b.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            b.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
            b.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        });

        modelBuilder.Entity<AuditExportJob>(b =>
        {
            b.ToTable("audit_export_jobs");
            b.HasKey(j => j.Id);
            b.Property(j => j.Id).HasColumnName("id");
            b.Property(j => j.Status).HasColumnName("status").HasConversion<string>();
            b.Property(j => j.RequestedBy).HasColumnName("requested_by");
            b.Property(j => j.RequestJson).HasColumnName("request_json").HasColumnType("jsonb");
            b.Property(j => j.StartedAt).HasColumnName("started_at");
            b.Property(j => j.CompletedAt).HasColumnName("completed_at");
            b.Property(j => j.DownloadUrl).HasColumnName("download_url");
            b.Property(j => j.Error).HasColumnName("error");
        });
    }
}

/// <summary>
/// L1.D-owned table: tracks async export jobs. Declared here in L0
/// alongside the DbContext so L1.D never has to touch this file —
/// L1.D adds the migration that creates the table and the worker that
/// fills it in.
/// </summary>
public sealed class AuditExportJob
{
    public Guid Id { get; set; }
    public Application.Export.AuditExportStatus Status { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public JsonDocument RequestJson { get; set; } = JsonDocument.Parse("{}");
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? DownloadUrl { get; set; }
    public string? Error { get; set; }
}
