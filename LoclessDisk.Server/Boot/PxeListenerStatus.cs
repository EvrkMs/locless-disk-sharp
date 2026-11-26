using LoclessDisk.Server.Events;

namespace LoclessDisk.Server.Boot;

public sealed class PxeListenerStatus
{
    private readonly ChangeSignal changes = new();
    private string message = "Службы PXE выключены.";

    public string Message => Volatile.Read(ref message);

    public IAsyncEnumerable<string> Stream(CancellationToken ct) =>
        changes.ReadAsync(_ => Task.FromResult(Message), ct);

    internal void Set(string value)
    {
        if (Interlocked.Exchange(ref message, value) != value)
            changes.Publish();
    }
}
