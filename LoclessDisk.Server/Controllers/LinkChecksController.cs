using LoclessDisk.Core;
using LoclessDisk.Server.Imaging;
using LoclessDisk.Server.Events;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/link-checks")]
public sealed class LinkChecksController(ILinkChecks tasks, IPxeComputers computers, IImageCatalog catalog, LinkCheckUpdates updates, DeploySessions deployments, CloneSessions clones) : ControllerBase
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

        if (input.Kind is not (AgentTaskKind.Check or AgentTaskKind.Format or AgentTaskKind.Deploy or AgentTaskKind.Clone))
            return Problem("Неизвестная задача.", statusCode: 400);
        if ((input.Kind is AgentTaskKind.Format or AgentTaskKind.Deploy) && !input.Confirmed)
            return Problem("Подтвердите запись на диск.", statusCode: 400);

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
                if (imageId is null || (!imageId.StartsWith("new:", StringComparison.Ordinal) &&
                    !(await catalog.ListAsync(ct)).Any(version => version.ImageId == imageId && !version.Disabled) &&
                !(input.Kind == AgentTaskKind.Clone && (await catalog.ListEmptyAsync(ct)).Any(image => image.Id == imageId))))
                    return Problem("Назначьте ПК доступный образ.", statusCode: 409);
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
