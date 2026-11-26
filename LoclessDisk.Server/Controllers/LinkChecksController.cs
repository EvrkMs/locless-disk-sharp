using LoclessDisk.Server.Boot;
using LoclessDisk.Server.Boot.GoAgent;
using LoclessDisk.Core;
using LoclessDisk.Server.Imaging;
using LoclessDisk.Server.Events;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/link-checks")]
public sealed class LinkChecksController(ILinkChecks tasks, IPxeComputers computers, IImageCatalog catalog, LinkCheckUpdates updates, DeploySessions deployments, CloneSessions clones,
    IPxeSettingsStore pxeSettings, IConfiguration configuration, IHostEnvironment environment) : ControllerBase
{
    [HttpGet("events")]
    public IResult Events(CancellationToken ct) => TypedResults.ServerSentEvents(updates.Stream(ct), "tasks");

    [HttpPost("{mac}")]
    public async Task<IActionResult> Queue(string mac, CancellationToken ct)
    {
        mac = mac.ToUpperInvariant();
        if (mac.Length != 12 || !mac.All(Uri.IsHexDigit))
            return Problem("Неверный MAC-адрес.", statusCode: 400);
        if (await tasks.QueueAsync(mac, AgentTaskKind.Check, null, ct) is not { } task)
            return Problem("ПК не зарегистрирован или у него уже есть активная проверка.", statusCode: 409);
        updates.Publish();
        return Ok(task);
    }

    [HttpPost("~/api/hosts/{mac}/task")]
    public async Task<IActionResult> QueueTask(string mac, TaskInput input, CancellationToken ct)
    {
        mac = mac.ToUpperInvariant();
        if (mac.Length != 12 || !mac.All(Uri.IsHexDigit))
            return Problem("Неверный MAC-адрес.", statusCode: 400);

        if (input.Kind is not (AgentTaskKind.Check or AgentTaskKind.CSharpProbe or AgentTaskKind.CSharpFormat or AgentTaskKind.Format or AgentTaskKind.Deploy or AgentTaskKind.Clone))
            return Problem("Неизвестная задача.", statusCode: 400);
        if ((input.Kind is AgentTaskKind.Format or AgentTaskKind.CSharpFormat or AgentTaskKind.Deploy) && !input.Confirmed)
            return Problem("Подтвердите запись на диск.", statusCode: 400);

        var options = configuration.GetSection("Pxe").Get<PxeOptions>() ?? new();
        var settings = await pxeSettings.ReadAsync(ct);
        bool builtIn = (settings?.Enabled ?? options.Enabled) &&
            string.IsNullOrWhiteSpace(settings?.BootScriptUrl ?? options.BootScriptUrl);
        bool csharp = input.Kind is AgentTaskKind.CSharpProbe or AgentTaskKind.CSharpFormat;
        if (csharp && !builtIn)
            return Problem("Для задачи C#-агента включите встроенную загрузку PXE.", statusCode: 409);
        if (builtIn)
        {
            string root = Path.GetFullPath(options.EfiRoot, environment.ContentRootPath);
            if (GoAgentEndpoints.FindMissingBootFile(root, csharp) is { } missing)
                return Problem("Задача требует загрузки WinPE. В папке EFI отсутствует " + missing + ".", statusCode: 409);
        }

        await ImagesController.ScanGate.WaitAsync(ct);
        try
        {
            string? imageId = null;
            if (input.Kind is AgentTaskKind.Clone or AgentTaskKind.Deploy)
            {
                if (input.Kind == AgentTaskKind.Clone && !string.IsNullOrWhiteSpace(input.NewImage))
                {
                    string name = input.NewImage.Trim();
                    if (!CloneSessions.IsValidName(name))
                        return Problem("Имя образа: 1–64 символа, латиница, цифры, дефис и подчёркивание.", statusCode: 400);
                    imageId = "new:" + name;
                }
                else
                {
                    imageId = (await computers.ListAsync(ct)).FirstOrDefault(pc => pc.Mac == mac)?.ImageId;
                }
                if (imageId is null)
                    return Problem("Назначьте ПК доступный образ.", statusCode: 409);
                if (!imageId.StartsWith("new:", StringComparison.Ordinal))
                {
                    var current = (await catalog.ListAsync(ct))
                        .FirstOrDefault(version => version.ImageId == imageId && !version.Disabled);
                    bool canCapture = input.Kind == AgentTaskKind.Clone && current is null &&
                        (await catalog.ListEmptyAsync(ct)).Any(image => image.Id == imageId);
                    if (!canCapture && (current is null || !current.Paths.All(System.IO.File.Exists)))
                        return Problem("Файлы образа недоступны.", statusCode: 409);
                }
            }
            if (await tasks.QueueAsync(mac, input.Kind, imageId, ct) is not { } task)
                return Problem("ПК не зарегистрирован или у него уже есть активная задача.", statusCode: 409);
            updates.Publish();
            return Ok(task);
        }
        finally { ImagesController.ScanGate.Release(); }
    }
    public sealed record TaskInput(string? Kind, bool Confirmed, string? NewImage);

    [HttpGet("~/api/tasks/{id:long}/metrics")]
    public async Task<IActionResult> Metrics(long id, CancellationToken ct)
    {
        string? json = await tasks.ReadMetricsAsync(id, ct);
        return json is null ? NotFound() : Content(json, "application/json");
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        if (!await tasks.CancelAsync(id, ct))
            return Problem("Активная задача не найдена.", statusCode: 409);
        await deployments.AbortAsync(id);
        await clones.AbortAsync(id);
        updates.Publish();
        return NoContent();
    }
}
