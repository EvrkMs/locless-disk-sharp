using LoclessDisk.Server.Imaging;
using System.Net;
using LoclessDisk.Server.Boot.GoAgent;
using LoclessDisk.Server.Events;

namespace LoclessDisk.Server.Boot;

internal sealed class BootHttpServer(string efiRoot, string exitScript, IServiceScopeFactory scopes, LinkCheckUpdates updates, DeploySessions deployments, CloneSessions clones, ILogger log)
{
    private int listening;

    public bool IsListening => Volatile.Read(ref listening) != 0;

    public static string ScriptUrl(IPAddress address, int port) =>
        $"http://{address}:{port}/boot.ipxe?mac={{mac}}";

    public async Task ServeAsync(IPAddress address, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
                builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(address, port));
                builder.Services.AddRouting();
                await using var app = builder.Build();

                new GoAgentEndpoints($"http://{address}:{port}", efiRoot, exitScript, scopes, updates, deployments, clones, log).Map(app);

                await app.StartAsync(ct);
                Volatile.Write(ref listening, 1);
                log.LogInformation("PXE boot HTTP listening on {Address}:{Port}", address, port);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    Volatile.Write(ref listening, 0);
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await app.StopAsync(shutdown.Token);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (IOException error)
            {
                log.LogWarning(error, "PXE boot HTTP could not open {Address}:{Port}. Retrying in 5 seconds",
                    address, port);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}

internal static class BootScripts
{
    public static string Leave(string mode, string baseUrl) => mode switch
    {
        "exit" => "exit\n",
        "refind" => $"chain {baseUrl}/files/refind.efi || {LocalDisk}",
        _ => LocalDisk
    };

    private const string LocalDisk =
        "sanboot --no-describe --drive 0 --filename \\EFI\\Microsoft\\Boot\\bootmgfw.efi || sanboot --no-describe --drive 0 || exit\n";
}
