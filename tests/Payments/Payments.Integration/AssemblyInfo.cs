using Xunit;

// Force serial test execution within this assembly. Testcontainers postgres
// hits "connection refused" / EOF stream flakes when multiple tests open
// connections concurrently against the same container. Serial runs give
// the per-test EF retry budget room to recover transient hiccups.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
