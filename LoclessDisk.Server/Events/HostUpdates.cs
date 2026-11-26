using LoclessDisk.Core;

namespace LoclessDisk.Server.Events;

public sealed class HostUpdates(IServiceScopeFactory scopes)
{
    private readonly ChangeSignal changes = new();

    public void Publish() => changes.Publish();

    public IAsyncEnumerable<IReadOnlyList<PxeComputer>> Stream(CancellationToken ct) =>
        changes.ReadAsync(ReadAsync, ct);

    private async Task<IReadOnlyList<PxeComputer>> ReadAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPxeComputers>().ListAsync(ct);
    }
}
