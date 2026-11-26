using LoclessDisk.Core;
using LoclessDisk.Server.Events;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/link-checks")]
public sealed class LinkChecksController(ILinkChecks tasks, LinkCheckUpdates updates) : ControllerBase
{
    [HttpGet("events")]
    public IResult Events(CancellationToken ct) => TypedResults.ServerSentEvents(updates.Stream(ct), "tasks");

    [HttpPost("{mac}")]
    public async Task<IActionResult> Queue(string mac, CancellationToken ct)
    {
        mac = mac.ToUpperInvariant();
        if (mac.Length != 12 || !mac.All(Uri.IsHexDigit))
            return Problem("Неверный MAC-адрес.", statusCode: 400);
        if (await tasks.QueueAsync(mac, ct) is not { } task)
            return Problem("ПК не зарегистрирован или у него уже есть активная проверка.", statusCode: 409);
        updates.Publish();
        return Ok(task);
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct)
    {
        if (!await tasks.CancelAsync(id, ct))
            return Problem("Активная проверка не найдена.", statusCode: 409);
        updates.Publish();
        return NoContent();
    }
}
