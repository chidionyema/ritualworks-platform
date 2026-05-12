namespace Haworks.Cdc.Application.Interfaces;

public interface ICdcRelay
{
    Task RunAsync(CancellationToken ct);
}
