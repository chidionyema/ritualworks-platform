# B3 — Catalog: publish CategoryUpdatedEvent + CategoryDeletedEvent

## Goal

Catalog currently publishes nothing when a category is renamed or deleted. Add two new contract events, publish them from the existing command handlers via the existing outbox + IDomainEventPublisher pattern, and add tests proving the publish.

## Phase / blocks-on

Phase 2. Blocks-on: B1 done. **Independent of B2 and B4** — runs in parallel with them.

## Inputs (read in order)

1. `docs/agent-briefs/search/README.md`.
2. `docs/agent-briefs/search-service-spec.md` §6 (catalog-side changes) — the exact event field shape.
3. `src/Contracts/Catalog/ProductCacheInvalidatedEvent.cs` — the existing pattern. Mirror it.
4. `src/Catalog/Catalog.Application/Categories/Commands/UpdateCategoryCommand.cs` (and its handler) — find the publish site.
5. `src/Catalog/Catalog.Application/Categories/Commands/DeleteCategoryCommand.cs` (and its handler).
6. `src/Catalog/Catalog.Infrastructure/DependencyInjection.cs` — confirm the `AddEntityFrameworkOutbox` + `IDomainEventPublisher` wiring you'll be writing through.
7. `tests/Catalog.Integration/` — pick one existing test that asserts `harness.Published.Any<SomeEvent>()` and use it as a copy-paste template.

If `UpdateCategoryCommand` or `DeleteCategoryCommand` doesn't exist in the catalog code: file a blocker. Do not invent them.

## Deliverable

### Contract records

`src/Contracts/Catalog/CategoryUpdatedEvent.cs`:

```csharp
namespace Haworks.Contracts.Catalog;

public sealed record CategoryUpdatedEvent : DomainEvent
{
    public required Guid CategoryId { get; init; }
    public required string Name { get; init; }
}
```

`src/Contracts/Catalog/CategoryDeletedEvent.cs`:

```csharp
namespace Haworks.Contracts.Catalog;

public sealed record CategoryDeletedEvent : DomainEvent
{
    public required Guid CategoryId { get; init; }
}
```

(Match the namespace and base type used by `ProductCacheInvalidatedEvent` — adjust the `using`/`namespace` lines to whatever the existing record uses.)

### Publish from handlers

In `UpdateCategoryCommand`'s handler, after the category is saved (in the same scope as the `await dbContext.SaveChangesAsync(...)` so it lands in the outbox transaction):

```csharp
await eventPublisher.PublishAsync(new CategoryUpdatedEvent
{
    CategoryId = category.Id,
    Name = category.Name,
}, ct);
```

In `DeleteCategoryCommand`'s handler, similarly:

```csharp
await eventPublisher.PublishAsync(new CategoryDeletedEvent
{
    CategoryId = command.CategoryId,
}, ct);
```

If the handlers don't already inject `IDomainEventPublisher`, add it via the constructor; do **not** restructure the handler beyond that.

### Tests

`tests/Catalog.Integration/CategoryEventsTests.cs` — two new tests using the existing WebApplicationFactory pattern:

1. `UpdateCategory_publishes_CategoryUpdatedEvent` — POST to the existing update endpoint (or send the command directly via MediatR), then assert the harness saw a `CategoryUpdatedEvent` with the expected `CategoryId` and new `Name`.
2. `DeleteCategory_publishes_CategoryDeletedEvent` — same shape.

If the existing tests don't go via the HTTP controller, mirror that — call MediatR directly or whatever the convention is.

## Acceptance

```bash
dotnet build RitualworksPlatform.sln -c Release
dotnet test tests/Catalog.Unit -c Release
dotnet test tests/Catalog.Integration -c Release --filter "FullyQualifiedName~CategoryEventsTests"
dotnet test tests/Catalog.Integration -c Release   # full suite — make sure nothing regresses
```

All green. Specifically the two new tests pass.

## Hard stops

- Do **not** modify any file in `src/Search/`.
- Do **not** modify any other Catalog domain entity or command beyond Update/Delete category.
- Do **not** add new fields to the events beyond what spec §6 lists. (No `OldName`, no `Reason` field, etc.)
- Do **not** rename existing events.
- Do **not** change the catalog's outbox / MassTransit wiring.
- If the existing handlers already publish a different event on category-update (e.g. `CategoryCacheInvalidatedEvent`), **do not delete it** — leave the existing event alone, add the new one alongside. Note in out-of-scope observations.

## Done-report

Standard format. Confirm:
- Both new tests pass.
- Catalog full integration suite still green.
- The publish happens inside the existing outbox transaction (i.e. `IDomainEventPublisher`, not a direct `IBus.Publish`) — explicitly state this.
