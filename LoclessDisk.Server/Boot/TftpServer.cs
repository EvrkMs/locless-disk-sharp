using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Tftp.Net;
using LibraryServer = Tftp.Net.TftpServer;

namespace LoclessDisk.Server.Boot;

internal sealed class TftpServer(string root, string scriptUrl, ILogger log)
{
    private const int TransferLimit = 64;

    public async Task ServeAsync(IPAddress address, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(new IPEndPoint(address, LibraryServer.DEFAULT_SERVER_PORT), ct);
            }
            catch (SocketException error)
            {
                log.LogWarning(error, "TFTP socket {Address}:69/UDP failed. Retrying in 5 seconds", address);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task ListenAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var active = new ConcurrentDictionary<ITftpTransfer, byte>();
        var admission = new object();
        bool stopping = false;
        using var server = new LibraryServer(endpoint);
        server.OnError += error => log.LogDebug("TFTP listener error: {Error}", error);
        server.OnWriteRequest += (transfer, _) => Reject(transfer, TftpErrorPacket.AccessViolation);
        server.OnReadRequest += (transfer, client) =>
        {
            lock (admission)
            {
                if (stopping || active.Count >= TransferLimit)
                {
                    Reject(transfer, new TftpErrorPacket(0, "Server busy"));
                    return;
                }
                if (transfer.TransferMode != TftpTransferMode.octet)
                {
                    Reject(transfer, new TftpErrorPacket(4, "Expected an octet read request"));
                    return;
                }

                Stream stream;
                try
                {
                    stream = Open(transfer.Filename);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Reject(transfer, error is FileNotFoundException or DirectoryNotFoundException
                        ? TftpErrorPacket.FileNotFound : TftpErrorPacket.AccessViolation);
                    return;
                }

                active.TryAdd(transfer, 0);
                transfer.OnFinished += completed => Complete(completed, active);
                transfer.OnError += (failed, error) =>
                {
                    log.LogDebug("TFTP transfer of {File} to {Client} stopped: {Error}",
                        failed.Filename, client, error);
                    Complete(failed, active);
                };
                try
                {
                    transfer.Start(stream);
                }
                catch (Exception error) when (error is IOException or SocketException or InvalidOperationException)
                {
                    log.LogDebug(error, "TFTP could not start transfer of {File} to {Client}", transfer.Filename, client);
                    Complete(transfer, active);
                    stream.Dispose();
                }
            }
        };

        try
        {
            server.Start();
            log.LogInformation("TFTP listening on {Endpoint}/UDP", endpoint);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        finally
        {
            lock (admission)
                stopping = true;
            foreach (var transfer in active.Keys)
            {
                if (active.TryRemove(transfer, out _))
                    DisposeTransfer(transfer);
            }
        }
    }

    private void Complete(ITftpTransfer transfer, ConcurrentDictionary<ITftpTransfer, byte> active)
    {
        if (active.TryRemove(transfer, out _))
        {
            // Let the library finish its callback before disposing its transfer state.
            ThreadPool.QueueUserWorkItem(_ => DisposeTransfer(transfer));
        }
    }

    private void Reject(ITftpTransfer transfer, TftpErrorPacket reason)
    {
        try
        {
            transfer.Cancel(reason);
        }
        catch (SocketException error)
        {
            log.LogDebug(error, "TFTP could not send its rejection");
        }
        finally
        {
            DisposeTransfer(transfer);
        }
    }

    private void DisposeTransfer(ITftpTransfer transfer)
    {
        try
        {
            transfer.Dispose();
        }
        catch (Exception error) when (error is SocketException or IOException)
        {
            log.LogDebug(error, "TFTP could not close transfer of {File}", transfer.Filename);
        }
    }

    private Stream Open(string requested)
    {
        string name = requested.Replace('\\', '/').TrimStart('/');
        string[] parts = name.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." ||
            part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':') ||
            part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new UnauthorizedAccessException();

        if (name.Equals("autoexec.ipxe", StringComparison.OrdinalIgnoreCase))
        {
            string url = scriptUrl.Replace("{mac}", "${netX/mac}");
            return new MemoryStream(Encoding.ASCII.GetBytes(
                "#!ipxe\ndhcp || dhcp || goto disk\nchain " + url +
                " || goto disk\n:disk\nsanboot --no-describe --drive 0x80 || exit\n"), writable: false);
        }

        string path = root;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException();
        foreach (string part in parts)
        {
            path = Path.Combine(path, part);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException();
        }
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new FileNotFoundException();
        return File.OpenRead(path);
    }
}
