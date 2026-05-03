using Xunit;

// Force serial execution within this assembly. Same rationale as Payments.Integration:
// concurrent connections to a single Testcontainers postgres compound the
// macOS Docker proxy port-mapping flake. Serial runs give EF retry-on-failure
// breathing room.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
