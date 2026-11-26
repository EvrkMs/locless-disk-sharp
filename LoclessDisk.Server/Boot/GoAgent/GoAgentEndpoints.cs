using LoclessDisk.Server.Imaging;
using System.Net.NetworkInformation;
using System.Text.Json;
using LoclessDisk.Core;
using LoclessDisk.Server.Events;
using Microsoft.AspNetCore.Http.Features;

namespace LoclessDisk.Server.Boot.GoAgent;

internal sealed partial class GoAgentEndpoints(
    string baseUrl, string efiRoot, string exitScript, IServiceScopeFactory scopes, LinkCheckUpdates updates, DeploySessions deployments, CloneSessions clones, ILogger log)
{
    private const string ConfigFile = "fogs-agent.json";
    private const string Shell =
        "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\wpeinit.exe\r\n%SYSTEMROOT%\\System32\\partclone.exe\r\n";

    private const string CSharpShell =
        "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\wpeinit.exe\r\n%SYSTEMROOT%\\System32\\LoclessDisk.Agent.exe, --pxe\r\n";
    internal const string CSharpAgentFile = "agent/LoclessDisk.Agent.exe";
    private const string GoAgentFile = "agent/partclone.exe";
    private const string BootWim = "WinPE/boot.wim";

    private sealed record ProgressReport(string? Mac, int Percent, string? Stage, string? Message, JsonElement? Metrics);
    private sealed record FinishReport(string? Mac, bool Ok, string? Message, JsonElement? Metrics);

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
                return Script(exitScript);

            bool csharp = task.Kind is AgentTaskKind.CSharpProbe or AgentTaskKind.CSharpFormat;
            string agent = csharp ? CSharpAgentFile : GoAgentFile;
            string shell = csharp ? "winpeshl-csharp.ini" : "winpeshl.ini";
            string? missing = FindMissingBootFile(efiRoot, csharp);
            if (missing is not null)
            {
                log.LogWarning("PXE task {Task} ({Kind}) for {Mac} is missing {File}", task.Id, task.Kind, key, missing);
                return Script(exitScript);
            }

            log.LogInformation("PXE task {Task} ({Kind}) for {Mac} uses {Agent}", task.Id, task.Kind, key, agent);
            return Script($"""
                kernel {baseUrl}/files/wimboot index=1 || goto disk
                initrd --name winpeshl.ini {baseUrl}/{shell} winpeshl.ini || goto disk
                initrd {baseUrl}/agent/{formatted.Replace(':', '-')}/{ConfigFile} {ConfigFile} || goto disk
                initrd {baseUrl}/files/{agent} {Path.GetFileName(agent)} || goto disk
                initrd {baseUrl}/files/WinPE/boot.wim boot.wim || goto disk
                boot || goto disk
                :disk
                """ + "\n" + exitScript);
        });

        app.MapGet("/winpeshl.ini", () => Results.Text(Shell, "text/plain"));
        app.MapGet("/winpeshl-csharp.ini", () => Results.Text(CSharpShell, "text/plain"));
        string[] files = ["wimboot", BootWim, GoAgentFile, CSharpAgentFile, "refind.efi"];
        foreach (string file in files)
        {
            app.MapMethods("/files/" + file, ["GET", "HEAD"], () =>
            {
                string? path = FindFile(efiRoot, file);
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
            if (task is null)
                return Results.NoContent();
            var computers = scope.ServiceProvider.GetRequiredService<IPxeComputers>();
            byte[]? table = task.Kind is AgentTaskKind.Format or AgentTaskKind.CSharpFormat ? null : await computers.ReadRecoveryTableAsync(key, ct);
            string? imageName = task.ImageId;
            if (imageName?.StartsWith("new:", StringComparison.Ordinal) == true)
                imageName = imageName[4..];
            else if (imageName is not null)
            {
                var catalog = scope.ServiceProvider.GetRequiredService<IImageCatalog>();
                imageName = (await catalog.ListEmptyAsync(ct)).FirstOrDefault(image => image.Id == task.ImageId)?.Name ??
                    (await catalog.ListAsync(ct)).FirstOrDefault(version => version.ImageId == task.ImageId)?.Name;
            }
            return Results.Json(new { id = task.Id, kind = task.Kind, image = imageName ?? "", table });
        });

        app.MapPost("/agent/tasks/{id:long}/progress", async (long id, HttpContext context, CancellationToken ct) =>
        {
            var input = await ReadReportAsync<ProgressReport>(context, ct);
            if (input is null || input.Percent is < 0 or > 100 || !TryMac(input.Mac, out string mac, out _))
                return Results.BadRequest("Invalid progress report.");

            await using var scope = scopes.CreateAsyncScope();
            var tasks = scope.ServiceProvider.GetRequiredService<ILinkChecks>();
            await SaveMetricsAsync(tasks, id, mac, input.Metrics, ct);
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
            await SaveMetricsAsync(tasks, id, mac, input.Metrics, ct);
            if (!await tasks.FinishAsync(id, mac, input.Ok, Clip(input.Message), ct))
                return Results.StatusCode(StatusCodes.Status410Gone);
            await deployments.AbortAsync(id);
            await clones.AbortAsync(id);
            updates.Publish();
            return Results.NoContent();
        });
        MapImageRoutes(app);
    }

    private static Task SaveMetricsAsync(ILinkChecks tasks, long id, string mac, JsonElement? metrics,
        CancellationToken ct)
    {
        return metrics is { ValueKind: JsonValueKind.Object } report
            ? tasks.SaveMetricsAsync(id, mac, report.GetRawText(), ct) : Task.CompletedTask;
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

    internal static string? FindMissingBootFile(string efiRoot, bool csharp)
    {
        string[] required = ["wimboot", BootWim, csharp ? CSharpAgentFile : GoAgentFile];
        return required.FirstOrDefault(file => FindFile(efiRoot, file) is null);
    }

    private static string? FindFile(string efiRoot, string file)
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
