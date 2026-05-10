using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haworks.Audit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEventsPartitioned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE audit_events (
    id              UUID         NOT NULL DEFAULT gen_random_uuid(),
    occurred_at     TIMESTAMPTZ  NOT NULL,
    received_at     TIMESTAMPTZ  NOT NULL DEFAULT now(),
    event_type      TEXT         NOT NULL,
    entity_type     TEXT         NOT NULL,    -- 'order', 'user', etc.
    entity_id       TEXT         NOT NULL,
    actor_id        TEXT,                     -- nullable: system-emitted events
    actor_type      TEXT,                     -- 'user', 'system', 'webhook'
    correlation_id  TEXT,                     -- saga / request correlation
    payload         JSONB        NOT NULL,    -- full event body, secrets stripped
    metadata        JSONB        NOT NULL,    -- routing key, source service, message_id, etc.
    PRIMARY KEY (id, occurred_at)
) PARTITION BY RANGE (occurred_at);

-- monthly partitions for May and June 2026
CREATE TABLE audit_events_2026_05 PARTITION OF audit_events
    FOR VALUES FROM ('2026-05-01') TO ('2026-06-01');

CREATE TABLE audit_events_2026_06 PARTITION OF audit_events
    FOR VALUES FROM ('2026-06-01') TO ('2026-07-01');

-- Indexes
CREATE INDEX audit_events_entity_idx
    ON audit_events (entity_type, entity_id, occurred_at DESC);
CREATE INDEX audit_events_correlation_idx
    ON audit_events (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX audit_events_event_type_idx
    ON audit_events (event_type, occurred_at DESC);

-- Idempotency on message_id (per-partition partial unique index)
CREATE UNIQUE INDEX audit_events_msg_id_uniq_2026_05
    ON audit_events_2026_05 ((metadata->>'message_id'))
    WHERE metadata->>'message_id' IS NOT NULL;

CREATE UNIQUE INDEX audit_events_msg_id_uniq_2026_06
    ON audit_events_2026_06 ((metadata->>'message_id'))
    WHERE metadata->>'message_id' IS NOT NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS audit_events CASCADE;");
        }
    }
}
