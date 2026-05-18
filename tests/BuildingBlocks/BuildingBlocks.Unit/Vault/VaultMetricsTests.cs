using System.Diagnostics.Metrics;
using FluentAssertions;
using Haworks.BuildingBlocks.Vault;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Vault;

public class VaultMetricsTests
{
    [Fact]
    public void MeterName_IsHaworksVault()
    {
        VaultMetrics.Meter.Name.Should().Be("Haworks.Vault");
    }

    [Fact]
    public void AuthSuccess_Counter_IsRegistered()
    {
        VaultMetrics.AuthSuccess.Should().NotBeNull();
        VaultMetrics.AuthSuccess.Name.Should().Be("vault.auth.success");

        // Verify it can record without throwing
        VaultMetrics.AuthSuccess.Add(1);
    }

    [Fact]
    public void AuthFailure_Counter_IsRegistered()
    {
        VaultMetrics.AuthFailure.Should().NotBeNull();
        VaultMetrics.AuthFailure.Name.Should().Be("vault.auth.failure");

        VaultMetrics.AuthFailure.Add(1);
    }

    [Fact]
    public void CredentialRotationDuration_Histogram_IsRegistered()
    {
        VaultMetrics.CredentialRotationDuration.Should().NotBeNull();
        VaultMetrics.CredentialRotationDuration.Name.Should().Be("vault.credential_rotation.duration_seconds");

        VaultMetrics.CredentialRotationDuration.Record(0.5);
    }

    [Fact]
    public void KvReadDuration_Histogram_IsRegistered()
    {
        VaultMetrics.KvReadDuration.Should().NotBeNull();
        VaultMetrics.KvReadDuration.Name.Should().Be("vault.kv.read.duration_seconds");

        VaultMetrics.KvReadDuration.Record(0.1);
    }

    [Fact]
    public void AllInstruments_AreDiscoverableViaMeterListener()
    {
        var instrumentNames = new List<string>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, "Haworks.Vault", StringComparison.Ordinal))
            {
                instrumentNames.Add(instrument.Name);
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();

        // Trigger instrument publishing by recording values
        VaultMetrics.AuthSuccess.Add(1);
        VaultMetrics.AuthFailure.Add(1);
        VaultMetrics.CredentialRotationDuration.Record(0.1);
        VaultMetrics.CredentialRotationFailure.Add(1);
        VaultMetrics.KvReadDuration.Record(0.1);
        VaultMetrics.KvReadFailure.Add(1);

        instrumentNames.Should().Contain("vault.auth.success");
        instrumentNames.Should().Contain("vault.auth.failure");
        instrumentNames.Should().Contain("vault.credential_rotation.duration_seconds");
        instrumentNames.Should().Contain("vault.kv.read.duration_seconds");
    }
}
