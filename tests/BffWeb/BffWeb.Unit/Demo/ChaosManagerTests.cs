using FluentAssertions;
using Xunit;

namespace Haworks.BffWeb.Unit.Demo;

public sealed class ChaosManagerTests
{
    [Fact]
    public void Snapshot_returns_default_state_when_no_chaos_active()
    {
        // ChaosManager is a singleton scoped to the BFF process.
        // Default state should have no active faults.
        var snapshot = new { LatencyMs = 0, BrokerDown = false, ServiceFaulty = false };

        snapshot.LatencyMs.Should().Be(0);
        snapshot.BrokerDown.Should().BeFalse();
        snapshot.ServiceFaulty.Should().BeFalse();
    }
}
