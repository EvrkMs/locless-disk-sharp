using System.Net;
using System.Net.Sockets;
using LoclessDisk.Core;

namespace LoclessDisk.Server.Boot;

internal sealed class PxeOptions
{
    public bool Enabled { get; set; }
    public string? BindAddress { get; set; }
    public string EfiRoot { get; set; } = "EFI";
    public string BootFile { get; set; } = "ipxe-shim.efi";
    public string BootScriptUrl { get; set; } = "";
}

internal sealed class PxeHostedService(IConfiguration configuration, IHostEnvironment environment, IServiceScopeFactory scopes,
    ILogger<PxeHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configuration.GetSection("Pxe").Get<PxeOptions>() ?? new();
        PxeSettings? applied = null;
        CancellationTokenSource? listenerLifetime = null;
        Task listeners = Task.CompletedTask;
        using var poll = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            do
            {
                await using var scope = scopes.CreateAsyncScope();
                var saved = await scope.ServiceProvider.GetRequiredService<IPxeSettingsStore>()
                    .ReadAsync(stoppingToken);
                var desired = saved ?? new PxeSettings(options.Enabled, options.BindAddress);
                if (desired == applied)
                    continue;

                await StopListenersAsync();
                applied = desired;
                if (desired.Enabled)
                {
                    listenerLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    listeners = RunPxeAsync(options, desired.BindAddress, listenerLifetime.Token);
                }
            }
            while (await poll.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await StopListenersAsync(); }

        async Task StopListenersAsync()
        {
            if (listenerLifetime is null)
                return;
            await listenerLifetime.CancelAsync();
            try { await listeners; }
            catch (OperationCanceledException) when (listenerLifetime.IsCancellationRequested) { }
            finally
            {
                listenerLifetime.Dispose();
                listenerLifetime = null;
            }
        }
    }

    private async Task RunPxeAsync(PxeOptions options, string? bindAddress, CancellationToken stoppingToken)
    {
        if (!IPAddress.TryParse(bindAddress, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224)
        {
            log.LogWarning("PXE is enabled, but its listening IPv4 is invalid");
            return;
        }

        string root;
        try
        {
            root = ValidateBoot(options, environment.ContentRootPath);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
        {
            log.LogWarning(error, "PXE configuration is incomplete");
            return;
        }

        var dhcp = new ProxyDhcp(address, options, ObserveAsync, log);

        async Task<bool> ObserveAsync(string mac, CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var computers = scope.ServiceProvider.GetRequiredService<IPxeComputers>();
            try
            {
                return await computers.ObserveAsync(mac, ct);
            }
            catch (System.Data.Common.DbException error)
            {
                log.LogWarning(error, "Could not record PXE client {Mac}", mac);
                return false;
            }
        }
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

    internal static string ValidateBoot(PxeOptions options, string contentRoot)
    {
        string root = Path.GetFullPath(options.EfiRoot, contentRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"PXE EFI directory does not exist: {root}");

        if (!IsBootField(options.BootFile) ||
            !Uri.TryCreate(options.BootScriptUrl.Replace("{mac}", "00:00:00:00:00:00"),
                UriKind.Absolute, out var script) ||
            script.Scheme is not ("http" or "https") || !IsBootField(script.OriginalString))
            throw new InvalidOperationException("Configure a valid Pxe:BootFile and HTTP(S) Pxe:BootScriptUrl.");

        return root;
    }

    private static bool IsBootField(string value) =>
        value.Length is > 0 and <= 127 && value.All(c => c is >= '!' and <= '~');
}
