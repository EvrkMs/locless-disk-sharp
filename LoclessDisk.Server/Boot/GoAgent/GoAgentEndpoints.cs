using System.Net.NetworkInformation;
using System.Text.Json;
using LoclessDisk.Core;
using LoclessDisk.Server.Events;
using Microsoft.AspNetCore.Http.Features;

namespace LoclessDisk.Server.Boot.GoAgent;

internal sealed class GoAgentEndpoints(
    string baseUrl, string efiRoot, IServiceScopeFactory scopes, LinkCheckUpdates updates, ILogger log)
{
    private const string ConfigFile = "fogs-agent.json";
    private const string Shell =
        "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\wpeinit.exe\r\n%SYSTEMROOT%\\System32\\partclone.exe\r\n";

    private static readonly string[] BootFiles = ["wimboot", "WinPE/boot.wim", "agent/partclone.exe"];

    private sealed record ProgressReport(string? Mac, int Percent, string? Stage, string? Message);
    private sealed record FinishReport(string? Mac, bool Ok, string? Message);

    public void Map(WebApplication app)
    {
        app.MapGet("/boot.ipxe", async (string? mac, CancellationToken ct) =>
        {
            if (!TryMac(mac, out string key, out string formatted))
                return Results.BadRequest("Invalid MAC address.");

            await using var scope = scopes.CreateAsyncScope();
            var tasks = scope.ServiceProvider.GetRequiredService<ILinkChecks>();
            var task = await tasks.GetActiveAsync(key, ct);
            if (task is null)
                return Script(BootScripts.LocalDisk);

            string? missing = BootFiles.FirstOrDefault(file => FindFile(file) is null);
            if (missing is not null)
            {
                log.LogWarning("Boot check {Task} for {Mac} awaits EFI file {File}", task.Id, key, missing);
                return Script(BootScripts.LocalDisk);
            }

            log.LogInformation("Sending WinPE script for link check {Task}, PC {Mac}", task.Id, key);
            return Script($"""
                kernel {baseUrl}/files/wimboot index=1 || goto disk
                initrd {baseUrl}/winpeshl.ini winpeshl.ini || goto disk
                initrd {baseUrl}/agent/{formatted.Replace(':', '-')}/{ConfigFile} {ConfigFile} || goto disk
                initrd {baseUrl}/files/agent/partclone.exe partclone.exe || goto disk
                initrd {baseUrl}/files/WinPE/boot.wim boot.wim || goto disk
                boot || goto disk
                :disk
                """ + "\n" + BootScripts.LocalDisk);
        });

        app.MapGet("/winpeshl.ini", () => Results.Text(Shell, "text/plain"));
        foreach (string file in BootFiles)
        {
            app.MapMethods("/files/" + file, ["GET", "HEAD"], () =>
            {
                string? path = FindFile(file);
                return path is null ? Results.NotFound() :
                    Results.File(path, "application/octet-stream", enableRangeProcessing: true);
            });
        }

        app.MapGet("/agent/{mac}/" + ConfigFile, async (string mac, CancellationToken ct) =>
        {
            if (!TryMac(mac, out string key, out string formatted))
                return Results.BadRequest("Invalid MAC address.");

            await using var scope = scopes.CreateAsyncScope();
            var hosts = scope.ServiceProvider.GetRequiredService<IPxeComputers>();
            string? name = await hosts.GetRegisteredNameAsync(key, ct);
            return name is null ? Results.NotFound() : Results.Json(new { server = baseUrl, mac = formatted, host = name });
        });

        app.MapGet("/agent/task", async (string? mac, CancellationToken ct) =>
        {
            if (!TryMac(mac, out string key, out _))
                return Results.BadRequest("Invalid MAC address.");

            await using var scope = scopes.CreateAsyncScope();
            var tasks = scope.ServiceProvider.GetRequiredService<ILinkChecks>();
            var task = await tasks.GetActiveAsync(key, ct);
            return task is null ? Results.NoContent() : Results.Json(new { id = task.Id, kind = "agent" });
        });

        app.MapPost("/agent/tasks/{id:long}/progress", async (long id, HttpContext context, CancellationToken ct) =>
        {
            var input = await ReadReportAsync<ProgressReport>(context, ct);
            if (input is null || input.Percent is < 0 or > 100 || !TryMac(input.Mac, out string mac, out _))
                return Results.BadRequest("Invalid progress report.");

            await using var scope = scopes.CreateAsyncScope();
            var tasks = scope.ServiceProvider.GetRequiredService<ILinkChecks>();
            if (!await tasks.ReportAsync(id, mac, input.Percent, Clip(input.Stage), Clip(input.Message), ct))
                return Results.StatusCode(StatusCodes.Status410Gone);
            updates.Publish();
            return Results.NoContent();
        });

        app.MapPost("/agent/tasks/{id:long}/finish", async (long id, HttpContext context, CancellationToken ct) =>
        {
            var input = await ReadReportAsync<FinishReport>(context, ct);
            if (input is null || !TryMac(input.Mac, out string mac, out _))
                return Results.BadRequest("Invalid result report.");

            await using var scope = scopes.CreateAsyncScope();
            var tasks = scope.ServiceProvider.GetRequiredService<ILinkChecks>();
            if (!await tasks.FinishAsync(id, mac, input.Ok, Clip(input.Message), ct))
                return Results.StatusCode(StatusCodes.Status410Gone);
            updates.Publish();
            return Results.NoContent();
        });
    }

    private static IResult Script(string body) =>
        Results.Text("#!ipxe\n" + body, "text/plain; charset=utf-8");

    private static bool TryMac(string? mac, out string key, out string formatted)
    {
        key = "";
        formatted = "";
        if (!PhysicalAddress.TryParse(mac?.Replace(':', '-'), out var parsed) ||
            parsed.GetAddressBytes().Length != 6)
            return false;

        key = parsed.ToString();
        formatted = string.Join(':', parsed.GetAddressBytes().Select(value => value.ToString("x2")));
        return true;
    }

    private string? FindFile(string file)
    {
        string path = efiRoot;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return null;
            foreach (string part in file.Split('/'))
            {
                path = Path.Combine(path, part);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return null;
            }
            return File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<T?> ReadReportAsync<T>(HttpContext context, CancellationToken ct) where T : class
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limits)
            limits.MaxRequestBodySize = 32 * 1024;
        if (!context.Request.HasJsonContentType())
            return null;
        try { return await context.Request.ReadFromJsonAsync<T>(ct); }
        catch (JsonException) { return null; }
    }

    private static string Clip(string? value) =>
        value is null ? "" : string.Concat(value.EnumerateRunes().Take(500));
}
