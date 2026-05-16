```
REPO=/Users/chidionyema/Documents/code/haworks-platform
GH_REPO=chidionyema/haworks-platform
WAVE_MODE=new-service
BASE_BRANCH=feat/cdc-service
BRIEF_FILE=docs/agent-briefs/cdc/parallel-tracks.md
TRACK_PREFIX=feat/cdc-
TRACKS=(T1 T2 T3 T4 T5)
WORKTREE_PARENT=/Users/chidionyema/.gemini/tmp/haworks-platform
```

## Universal rules

- **Execute, don't narrate.** No "let me first understand" prose. Output text is limited to: progress signals (one line per file group), error reports, and final Done-Report. Internal reasoning stays internal.
- **File-scope discipline.** Do not touch files outside your "Files you own" list. Period.
- **No exploration.** Do not grep, find, or ls outside your Files-you-own list unless required for compilation. The brief has the exact paths you need.
- **No scope creep.** No tangential edits. If you notice an obvious improvement outside your listed file paths, write a // TODO(track-Tn) comment in YOUR file and continue.
- **Build verify.** Before every commit, build the projects you touched: `dotnet build src/Cdc/Cdc.<Project> --nologo --verbosity quiet`.
- **Done check.** Run the `Done:` shell command verbatim. It must exit 0.

## Anti-stuck

- **60-second decision limit.** Naming, location, or dependency choice taking too long? Mirror the reference file and move on.
- **Spec ambiguous?** Pick the simplest option that fulfills the intent, add a `// TODO(track-Tn)`, and proceed.
- **No questions to user.** Operator is not in session. Decide locally.

## Reference file

The platform-canonical service pattern is `src/Notifications/`. Mirror its DependencyInjection, Controller, and Consumer structures.

---

### Track T1: Relay Engine

**Files you own (exclusive):**
- `src/Cdc/Cdc.Contracts/EntityChangedEvent.cs`
- `src/Cdc/Cdc.Application/Relay/**`
- `src/Cdc/Cdc.Infrastructure/Relay/**`
- `src/Cdc/Cdc.Application/DependencyInjection.T1.cs`
- `tests/Cdc.Unit/Relay/**`

**Files you may NOT touch:**
- `src/Cdc/Cdc.Application/DependencyInjection.cs`
- `src/Cdc/Cdc.Infrastructure/Persistence/CdcDbContext.cs`

**Reference to mirror:** `src/Notifications/Notifications.Application/Consumers/NotificationRequestConsumer.cs`

**NuGet (if any):** `Npgsql`

**Done:** `dotnet test tests/Cdc.Unit/Cdc.Unit.csproj --filter "FullyQualifiedName~Relay"`

**Work plan:**
1. **Model** — Create `EntityChangedEvent.cs`:
   ```csharp
   public record EntityChangedEvent(
       Guid EventId, string EntityType, string EntityId, 
       string ChangeType, DateTimeOffset OccurredAt,
       JsonElement? PayloadBefore, JsonElement? PayloadAfter);
   ```
2. **Relay Loop** — Implement `ReplicationSubscriber.cs`:
   ```csharp
   public sealed class ReplicationSubscriber(NpgsqlConnection conn) {
       public async Task StartAsync(string slotName, CancellationToken ct) {
           using var replConn = new LogicalReplicationConnection(conn.ConnectionString);
           await replConn.Open(ct);
           // ... process WAL messages via pgoutput ...
       }
   }
   ```
3. **Publisher** — Implement `CdcEventPublisher.cs`:
   ```csharp
   public sealed class CdcEventPublisher(IPublishEndpoint publishEndpoint) {
       public async Task PublishAsync(EntityChangedEvent @event, CancellationToken ct) {
           await publishEndpoint.Publish(@event, context => {
               context.SetRoutingKey($"{@event.EntityType}.{@event.ChangeType}");
           }, ct);
       }
   }
   ```
4. **DI** — Wire components in `DependencyInjection.T1.cs`.

**Reference (inline excerpt, lines 1-30 of spec § 3):**
```json
{
  "event_id": "uuid",
  "entity_type": "string",
  "entity_id": "string",
  "change_type": "created|updated|deleted",
  "payload_before": null | json,
  "payload_after": null | json
}
```

---

### Track T2: Admin & CLI

**Files you own (exclusive):**
- `src/Cdc/Cdc.Api/Controllers/CdcController.cs`
- `src/Cdc/Cdc.Application/Admin/**`
- `src/Cdc/Cdc.Application/DependencyInjection.T2.cs`
- `scripts/cdc`

**Files you may NOT touch:**
- `src/Cdc/Cdc.Application/DependencyInjection.cs`

**Reference to mirror:** `src/Notifications/Notifications.Api/Controllers/NotificationsController.cs`

**NuGet (if any):** none

**Done:** `dotnet build src/Cdc/Cdc.Api/Cdc.Api.csproj && [ -f scripts/cdc ]`

**Work plan:**
1. **Admin Handlers** — Create commands:
   ```csharp
   public record PauseSourceCommand(string ServiceName) : IRequest<Result>;
   public record ResumeSourceCommand(string ServiceName) : IRequest<Result>;
   ```
2. **Controller** — Create `CdcController.cs`:
   ```csharp
   [ApiController] [Route("api/cdc")]
   public sealed class CdcController(IMediator mediator) : ControllerBase {
       [HttpPost("sources/{name}/pause")]
       public async Task<IActionResult> Pause(string name) => (await mediator.Send(new PauseSourceCommand(name))).ToActionResult();
   }
   ```
3. **CLI** — Create `scripts/cdc` bash script that wraps `curl` calls to the Admin API, following the pattern in `scripts/stack.sh`.
4. **DI** — Wire in `DependencyInjection.T2.cs`.

---

### Track T3: Producer Enrolment

**Files you own (exclusive):**
- `infra/stateful/cdc-publications/**`
- `infra/stateful/postgres-clusters/**`
- `src/Cdc/Cdc.Infrastructure/Persistence/Migrations/*_AddCdcSources.cs`
- `src/Cdc/Cdc.Application/DependencyInjection.T3.cs`

**Files you may NOT touch:**
- `src/Cdc/Cdc.Infrastructure/Persistence/CdcDbContext.cs`

**Reference to mirror:** `fly.catalog.toml`

**NuGet (if any):** none

**Done:** `ls infra/stateful/cdc-publications/catalog.sql && dotnet build src/Cdc/Cdc.Infrastructure`

**Work plan:**
1. **SQL Publications** — Create `infra/stateful/cdc-publications/catalog.sql`:
   ```sql
   CREATE PUBLICATION cdc_publication FOR TABLE products, product_categories;
   ALTER TABLE products REPLICA IDENTITY FULL;
   ```
2. **Infra Config** — Update postgres cluster YAMLs in `infra/stateful/postgres-clusters/` to set `wal_level=logical`.
3. **Seed Data** — Create migration to add initial sources to `cdc_sources` and `cdc_table_map` tables per spec § 5.2.

---

### Track T4: Consumer Adaptation

**Files you own (exclusive):**
- `src/Search/Search.Application/Consumers/EntityChangedConsumer.cs`
- `src/Audit/Audit.Application/Consumers/DataAuditConsumer.cs`
- `src/Audit/Audit.Infrastructure/Persistence/Migrations/*_AddDataAuditEvents.cs`
- `src/Audit/Audit.Application/DependencyInjection.Cdc.cs`
- `src/Search/Search.Application/DependencyInjection.Cdc.cs`

**Files you may NOT touch:**
- `src/Search/Search.Application/DependencyInjection.cs`
- `src/Audit/Audit.Application/DependencyInjection.cs`

**Reference to mirror:** `src/Notifications/Notifications.Application/Consumers/NotificationRequestConsumer.cs`

**NuGet (if any):** none

**Done:** `dotnet build src/Search/Search.Application && dotnet build src/Audit/Audit.Application`

**Work plan:**
1. **Search Consumer** — Implement `EntityChangedConsumer.cs`:
   ```csharp
   public sealed class EntityChangedConsumer(ISearchIndex index) : IConsumer<EntityChangedEvent> {
       public async Task Consume(ConsumeContext<EntityChangedEvent> context) {
           var msg = context.Message;
           await index.UpdateAsync(msg.EntityType, msg.EntityId, msg.PayloadAfter);
       }
   }
   ```
2. **Audit Consumer** — Implement `DataAuditConsumer.cs`:
   ```csharp
   public sealed class DataAuditConsumer(AuditDbContext db) : IConsumer<EntityChangedEvent> {
       public async Task Consume(ConsumeContext<EntityChangedEvent> context) {
           db.DataAuditEvents.Add(new DataAuditEvent { /* map from context.Message */ });
           await db.SaveChangesAsync();
       }
   }
   ```
3. **Audit Migration** — Create migration for `data_audit_events` table in Audit service.
4. **Wiring** — Create `DependencyInjection.Cdc.cs` in both services to register the new consumers.

---

### Track T5: Cache & E2E

**Files you own (exclusive):**
- `src/BffWeb/BffWeb.Application/Consumers/CacheInvalidationConsumer.cs`
- `src/BffWeb/BffWeb.Application/DependencyInjection.Cdc.cs`
- `tests/E2E/Journeys/CdcJourney.cs`
- `infra/observability/grafana/dashboards/cdc.json`

**Files you may NOT touch:**
- `src/BffWeb/BffWeb.Application/DependencyInjection.cs`

**Reference to mirror:** `tests/E2E/CheckoutE2ETests.cs`

**NuGet (if any):** none

**Done:** `dotnet test tests/E2E/E2E.csproj --filter "FullyQualifiedName~CdcJourney"`

**Work plan:**
1. **Cache Invalidator** — Implement `CacheInvalidationConsumer.cs`:
   ```csharp
   public sealed class CacheInvalidationConsumer(IDistributedCache cache) : IConsumer<EntityChangedEvent> {
       public async Task Consume(ConsumeContext<EntityChangedEvent> context) {
           var key = $"{context.Message.EntityType}:{context.Message.EntityId}";
           await cache.RemoveAsync(key);
       }
   }
   ```
2. **Dashboard** — Create `cdc.json` Grafana dashboard.
3. **E2E Test** — Implement `CdcJourney.cs`:
   ```csharp
   [Fact] public async Task Product_Update_Propagates() {
       await host.Catalog.PutAsync("/products/1", new { price = 100 });
       await host.EventBus.Saw<EntityChangedEvent>(e => e.EntityId == "1");
       await host.Cache.AssertKeyAbsent("product:1");
   }
   ```
4. **Wiring** — Create `DependencyInjection.Cdc.cs` in BffWeb.

