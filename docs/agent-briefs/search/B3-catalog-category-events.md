# B3 — Catalog: publish CategoryUpdatedEvent

## Goal

Catalog currently publishes nothing when a category is renamed. Add a new contract event, publish it from the existing `UpdateCategoryCommand` handler via the existing outbox + `IDomainEventPublisher` pattern, and add an integration test proving the publish.

(Originally this brief also covered a `CategoryDeletedEvent`. The platform does not currently expose a `DeleteCategoryCommand` — the spec defers the delete event until that command is added in a future phase.)

## Phase / blocks-on

Phase 2. Blocks-on: B1 done. **Independent of B2 and B4** — runs in parallel with them.

## Inputs (read in order)

1. `docs/agent-briefs/search/README.md`.
2. `docs/agent-briefs/search-service-spec.md` §6 (catalog-side changes) — the exact event field shape.
3. `src/Contracts/Catalog/ProductCacheInvalidatedEvent.cs` — the existing pattern. Mirror it.
4. `src/Catalog/Catalog.Application/Commands/UpdateCategoryCommand.cs` (and its handler in the same file or alongside) — find the publish site. **Note the path: `Commands/`, not `Categories/Commands/`.**
5. `src/Catalog/Catalog.Infrastructure/DependencyInjection.cs` — confirm the `AddEntityFrameworkOutbox` + `IDomainEventPublisher` wiring you'll be writing through.
6. `tests/Catalog.Integration/` — pick one existing test that asserts `harness.Published.Any<SomeEvent>()` and use it as a copy-paste template.

If `UpdateCategoryCommand` doesn't exist where it should: file a blocker. Do not invent it.

## Deliverable

### Contract record

`src/Contracts/Catalog/CategoryUpdatedEvent.cs`:

```csharp
namespace Haworks.Contracts.Catalog;

public sealed record CategoryUpdatedEvent : DomainEvent
{
    public required Guid CategoryId { get; init; }
    public required string Name { get; init; }
}
```

(Match the namespace and base type used by `ProductCacheInvalidatedEvent` — adjust the `using`/`namespace` lines to whatever the existing record uses.)

### Publish from the handler

In `UpdateCategoryCommand`'s handler, after the category is saved (in the same scope as `await dbContext.SaveChangesAsync(...)` so it lands in the outbox transaction):

```csharp
await eventPublisher.PublishAsync(new CategoryUpdatedEvent
{
    CategoryId = category.Id,
    Name = category.Name,
}, ct);
```

If the handler doesn't already inject `IDomainEventPublisher`, add it via the constructor; do **not** restructure the handler beyond that.

### Tests

`tests/Catalog.Integration/CategoryEventsTests.cs` — one new test using the existing WebApplicationFactory pattern:

- `UpdateCategory_publishes_CategoryUpdatedEvent` — POST to the existing update endpoint (or send the command directly via MediatR — whichever the existing tests use), then assert the harness saw a `CategoryUpdatedEvent` with the expected `CategoryId` and new `Name`.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Catalog.Unit -c Release
dotnet test tests/Catalog.Integration -c Release --filter "FullyQualifiedName~CategoryEventsTests"
dotnet test tests/Catalog.Integration -c Release   # full suite — make sure nothing regresses
```

All green. Specifically the new test passes.

## Hard stops

- Do **not** modify any file in `src/Search/`.
- Do **not** modify any other Catalog domain entity or command beyond `UpdateCategoryCommand`.
- Do **not** create a `DeleteCategoryCommand` or a `CategoryDeletedEvent` — they're explicitly out of scope per the spec.
- Do **not** add new fields to the event beyond what spec §6 lists. (No `OldName`, no `Reason` field, etc.)
- Do **not** rename existing events.
- Do **not** change the catalog's outbox / MassTransit wiring.
- If the existing handler already publishes a different event on category-update (e.g. `CategoryCacheInvalidatedEvent`), **do not delete it** — leave the existing event alone, add the new one alongside. Note in out-of-scope observations.

## Done-report

Standard format. Confirm:
- The new test passes.
- Catalog full integration suite still green.
- The publish happens inside the existing outbox transaction (i.e. `IDomainEventPublisher`, not a direct `IBus.Publish`) — explicitly state this.
