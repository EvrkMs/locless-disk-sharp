using System.Net;
using System.Net.NetworkInformation;

namespace LoclessDisk.Server.Boot;

internal sealed class BootHttpServer(ILogger log)
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

                app.MapGet("/boot.ipxe", (HttpContext context) =>
                {
                    string mac = context.Request.Query["mac"].ToString();
                    if (!PhysicalAddress.TryParse(mac.Replace(':', '-'), out var parsed) ||
                        parsed.GetAddressBytes().Length != 6)
                        return Results.BadRequest("Invalid MAC address.");

                    // Sharp has no deployment tasks yet, so all clients continue to disk.
                    return Results.Text("#!ipxe\n" + BootScripts.LocalDisk, "text/plain; charset=utf-8");
                });

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
    public const string LocalDisk =
        "sanboot --no-describe --drive 0 --filename \\EFI\\Microsoft\\Boot\\bootmgfw.efi || sanboot --no-describe --drive 0 || exit\n";
}
