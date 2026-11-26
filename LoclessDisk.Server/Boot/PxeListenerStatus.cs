namespace LoclessDisk.Server.Boot;

public sealed class PxeListenerStatus
{
    private string message = "Службы PXE выключены.";

    public string Message => Volatile.Read(ref message);

    internal void Set(string value) => Volatile.Write(ref message, value);
}
