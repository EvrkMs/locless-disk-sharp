using LoclessDisk.Core;

namespace LoclessDisk.Server.Events;

public sealed class LinkCheckUpdates(IServiceScopeFactory scopes)
{
    private readonly ChangeSignal changes = new();

    public void Publish() => changes.Publish();

    public IAsyncEnumerable<IReadOnlyList<LinkCheck>> Stream(CancellationToken ct) =>
        changes.ReadAsync(ReadAsync, ct);

    private async Task<IReadOnlyList<LinkCheck>> ReadAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILinkChecks>().ListAsync(ct);
    }
}
