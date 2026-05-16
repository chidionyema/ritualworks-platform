# Contracts

Shared event contract library referenced by all services. Contains every cross-service domain event, command, and message type used on the MassTransit bus.

## Design Principles
- **Zero project references** — Contracts depends only on protobuf/gRPC tooling. Any dependency added here propagates to every service in the platform.
- **Immutable records** — all events use `{ get; init; }` properties and extend `DomainEvent`. Never use positional records (MassTransit `Init<T>` faults on them).
- **Namespace per service** — events are organized by owning service (e.g., `Haworks.Contracts.Catalog`, `Haworks.Contracts.Payments`).

## Event Namespaces
| Namespace | Examples |
|-----------|----------|
| Catalog | `ProductCreated`, `StockReserved`, `StockReservationFailed` |
| Checkout | `CheckoutStarted`, `CheckoutCompleted` |
| Identity | `UserRegistered`, `VaultRotationStageChanged` |
| Orders | `OrderPlaced`, `OrderCancelled`, `OrderStatusChanged` |
| Payments | `PaymentSessionCreated`, `PaymentCompleted`, `RefundInitiated` |
| Privacy | `ErasureRequested`, `ErasureCompleted` |
| Search | `SearchIndexRequested` |
| Location | `AddressValidated` |
| Media | `MediaUploaded`, `VirusScanCompleted` |
| Cdc | `EntityChangedEvent` |
| FeatureFlags | `FeatureFlagUpdated` |
| Localization | `TranslationUpdated` |

## Adding a New Event
1. Create a record class in the appropriate namespace directory
2. Use `{ get; init; }` properties — never positional records
3. Extend `DomainEvent` (provides `EventId`, `OccurredAt`, `CorrelationId`)
4. Rebuild — all services that reference Contracts will pick up the new type
