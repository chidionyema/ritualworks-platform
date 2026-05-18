Scaffold a new microservice following the platform conventions.

Usage: /new-service Shipping

Create the standard 4-layer Clean Architecture structure:
1. src/$ARGUMENTS/$ARGUMENTS.Domain/ — entities, value objects, events
2. src/$ARGUMENTS/$ARGUMENTS.Application/ — commands, queries, consumers, validators
3. src/$ARGUMENTS/$ARGUMENTS.Infrastructure/ — DbContext, DependencyInjection.cs, EF config
4. src/$ARGUMENTS/$ARGUMENTS.Api/ — Program.cs, Controllers, Dockerfile

Follow patterns from existing services (e.g., Catalog):
- DependencyInjection.cs with AddInfrastructure extension
- MassTransit + EF Outbox registration
- Health checks (DB + ready)
- AddServiceDefaults() in Program.cs
- Vault integration gate (Vault:Enabled)

Also create:
- fly.$ARGUMENTS.toml (copy from fly.catalog.toml, update app name)
- filters/$ARGUMENTS.slnf
- tests/$ARGUMENTS/$ARGUMENTS.Unit/ with at least one test
- Add to HaworksPlatform.sln
