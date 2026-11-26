using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LoclessDisk.Server.Boot;

internal sealed class PxeOptions
{
    public bool Enabled { get; set; }
    public string? BindAddress { get; set; }
    public string EfiRoot { get; set; } = "EFI";
    public string BootFile { get; set; } = "ipxe-shim.efi";
    public string BootScriptUrl { get; set; } = "";
    public string[] AllowedMacs { get; set; } = [];
}

internal sealed class PxeHostedService(IConfiguration configuration, IHostEnvironment environment,
    ILogger<PxeHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configuration.GetSection("Pxe").Get<PxeOptions>() ?? new();
        if (!options.Enabled)
            return;

        if (!IPAddress.TryParse(options.BindAddress, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224)
            throw new InvalidOperationException("Pxe:BindAddress must be a specific IPv4 address.");

        string root = Path.GetFullPath(options.EfiRoot, environment.ContentRootPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"PXE EFI directory does not exist: {root}");

        if (!IsBootField(options.BootFile) ||
            !Uri.TryCreate(options.BootScriptUrl.Replace("{mac}", "00:00:00:00:00:00"),
                UriKind.Absolute, out var script) ||
            script.Scheme is not ("http" or "https") || !IsBootField(script.OriginalString))
            throw new InvalidOperationException("Configure a valid Pxe:BootFile and HTTP(S) Pxe:BootScriptUrl.");

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string mac in options.AllowedMacs)
        {
            if (!PhysicalAddress.TryParse(mac.Replace(':', '-'), out var parsed) ||
                parsed.GetAddressBytes().Length != 6)
                throw new InvalidOperationException($"Invalid MAC in Pxe:AllowedMacs: {mac}");
            allowed.Add(parsed.ToString());
        }
        if (allowed.Count == 0)
            log.LogWarning("PXE allow list is empty. ProxyDHCP will stay silent");

        var dhcp = new ProxyDhcp(address, options, allowed, log);
        var tftp = new TftpServer(root, options.BootScriptUrl, log);
        try
        {
            await Task.WhenAll(
                RunListenerAsync(address, 67, true, (socket, ct) => dhcp.ServeAsync(socket, true, ct), stoppingToken),
                RunListenerAsync(address, 4011, false, (socket, ct) => dhcp.ServeAsync(socket, false, ct), stoppingToken),
                tftp.ServeAsync(address, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunListenerAsync(IPAddress address, int port, bool broadcast,
        Func<Socket, CancellationToken, Task> serve, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows())
                    socket.ExclusiveAddressUse = true;
                socket.EnableBroadcast = broadcast;
                socket.Bind(new IPEndPoint(address, port));
                log.LogInformation("PXE listening on {Address}:{Port}/UDP", address, port);
                await serve(socket, ct);
            }
            catch (SocketException error)
            {
                log.LogWarning(error, "PXE socket {Address}:{Port}/UDP failed. Retrying in 5 seconds",
                    address, port);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private static bool IsBootField(string value) =>
        value.Length is > 0 and <= 127 && value.All(c => c is >= '!' and <= '~');
}
