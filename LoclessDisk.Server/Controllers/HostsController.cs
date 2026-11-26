using System.Net.NetworkInformation;
using LoclessDisk.Core;
using LoclessDisk.Server.Events;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/hosts")]
public sealed class HostsController(IPxeComputers computers, ILinkChecks tasks, HostUpdates updates) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await computers.ListAsync(ct));

    [HttpGet("events")]
    public IResult Events(CancellationToken ct) => TypedResults.ServerSentEvents(updates.Stream(ct), "hosts");

    [HttpPost("{mac}/register")]
    public async Task<IActionResult> Register(string mac, Registration input, CancellationToken ct)
    {
        mac = mac.ToUpperInvariant();
        if (mac.Length != 12 || !mac.All(Uri.IsHexDigit))
            return Problem("Неверный MAC-адрес.", statusCode: 400);

        string? name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 64)
            return Problem("Укажите имя компьютера до 64 символов.", statusCode: 400);

        if (!await computers.RegisterAsync(mac, name, ct))
            return Problem("Компьютер уже добавлен или ещё не обнаружен.", statusCode: 409);
        updates.Publish();
        return Ok(new { mac, name });
    }

    [HttpPost]
    public Task<IActionResult> Add(ComputerInput input, CancellationToken ct) =>
        Save(input.Mac, input, create: true, ct);

    [HttpPut("{mac}")]
    public Task<IActionResult> Update(string mac, ComputerInput input, CancellationToken ct) =>
        Save(mac, input, create: false, ct);

    [HttpDelete("{mac}")]
    public Task<IActionResult> Delete(string mac, CancellationToken ct) => Remove(mac, true, ct);

    [HttpDelete("~/api/discovered/{mac}")]
    public Task<IActionResult> Hide(string mac, CancellationToken ct) => Remove(mac, false, ct);

    private async Task<IActionResult> Remove(string mac, bool registered, CancellationToken ct)
    {
        mac = mac.ToUpperInvariant();
        if (mac.Length != 12 || !mac.All(Uri.IsHexDigit))
            return Problem("Неверный MAC-адрес.", statusCode: 400);

        await ImagesController.ScanGate.WaitAsync(ct);
        try
        {
            if (registered && await tasks.GetActiveAsync(mac, ct) is not null)
                return Conflict("Сначала отмените активную задачу ПК.");
            if (registered && await computers.ReadRecoveryTableAsync(mac, ct) is not null)
                return Conflict("Сначала восстановите размер тома через «Проверить связь».");
            if (!await computers.RemoveAsync(mac, registered, ct))
                return NotFound();
            updates.Publish();
            return NoContent();
        }
        finally { ImagesController.ScanGate.Release(); }
    }

    private async Task<IActionResult> Save(string? mac, ComputerInput input, bool create, CancellationToken ct)
    {
        if (!PhysicalAddress.TryParse(mac?.Trim().Replace(':', '-'), out var address) ||
            address.GetAddressBytes().Length != 6)
            return Problem("Неверный MAC-адрес.", statusCode: 400);

        string? name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 64)
            return Problem("Укажите имя компьютера до 64 символов.", statusCode: 400);

        string? imageId = string.IsNullOrWhiteSpace(input.ImageId) ? null : input.ImageId.Trim().ToUpperInvariant();
        string key = address.ToString();
        var result = await computers.SaveAsync(key, name, imageId, create, ct);
        if (result == ComputerSaveResult.ImageNotFound)
            return Problem("Образ не найден в каталоге.", statusCode: 422);
        if (result == ComputerSaveResult.Conflict)
            return Problem(create ? "Компьютер уже добавлен." : "Компьютер не найден.", statusCode: 409);

        updates.Publish();
        return Ok(new { mac = key, name, imageId });
    }

    public sealed record ComputerInput(string? Mac, string? Name, string? ImageId);

    public sealed record Registration(string? Name);
}
