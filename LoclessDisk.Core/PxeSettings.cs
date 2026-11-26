namespace LoclessDisk.Core;

public sealed record PxeSettings(bool Enabled, string? BindAddress);

public interface IPxeSettingsStore
{
    Task<PxeSettings?> ReadAsync(CancellationToken cancellationToken);
    Task SaveAsync(PxeSettings settings, CancellationToken cancellationToken);
}
