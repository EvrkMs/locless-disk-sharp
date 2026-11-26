using LoclessDisk.Core;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/hosts")]
public sealed class HostsController(IPxeComputers computers) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await computers.ListAsync(ct));

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
        return Ok(new { mac, name });
    }

    public sealed record Registration(string? Name);
}
