namespace LoclessDisk.Core;

public sealed record PxeComputer(string Mac, string? Name, long LastSeen);

public interface IPxeComputers
{
    Task<bool> ObserveAsync(string mac, CancellationToken cancellationToken);
    Task<IReadOnlyList<PxeComputer>> ListAsync(CancellationToken cancellationToken);
    Task<bool> RegisterAsync(string mac, string name, CancellationToken cancellationToken);
}
