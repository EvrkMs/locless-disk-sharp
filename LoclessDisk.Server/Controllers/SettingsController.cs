using LoclessDisk.Core;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(IImageCatalog catalog) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(new SettingsInput(await catalog.GetRootAsync(ct)));

    [HttpPut]
    public async Task<IActionResult> Put(SettingsInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.ImageRoot) || !Path.IsPathFullyQualified(input.ImageRoot))
            return Problem("Укажите абсолютный путь к папке образов.", statusCode: 400);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.ImageRoot));
        if (!Directory.Exists(root))
            return Problem("Папка образов недоступна.", statusCode: 400);

        await catalog.SetRootAsync(root, ct);
        return Ok(new SettingsInput(root));
    }

    public sealed record SettingsInput(string? ImageRoot);
}
