namespace LoclessDisk.Core;

public sealed record PxeComputer(string Mac, string? Name, long LastSeen, string? ImageId);

public readonly record struct PxeObservation(bool Registered, bool Updated);

public enum ComputerSaveResult { Saved, Conflict, ImageNotFound }

public interface IPxeComputers
{
    Task<PxeObservation> ObserveAsync(string mac, CancellationToken cancellationToken);
    Task<string?> GetRegisteredNameAsync(string mac, CancellationToken cancellationToken);
    Task<IReadOnlyList<PxeComputer>> ListAsync(CancellationToken cancellationToken);
    Task<ComputerSaveResult> SaveAsync(string mac, string name, string? imageId, bool create,
        CancellationToken cancellationToken);
    Task<byte[]?> ReadRecoveryTableAsync(string mac, CancellationToken ct);
    Task<bool> SaveRecoveryTableAsync(string mac, byte[]? table, CancellationToken ct);
    Task<bool> RemoveAsync(string mac, bool registered, CancellationToken ct);
    Task<bool> RegisterAsync(string mac, string name, CancellationToken cancellationToken);
}
