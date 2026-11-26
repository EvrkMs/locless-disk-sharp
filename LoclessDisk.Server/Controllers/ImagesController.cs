using System.Text.Json;
using LoclessDisk.Core;
using LoclessDisk.Images;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/images")]
public sealed class ImagesController(IImageCatalog catalog, ILogger<ImagesController> logger) : ControllerBase
{
    private static readonly SemaphoreSlim ScanGate = new(1);

    [HttpPost("scan")]
    public async Task<IActionResult> Scan([FromBody] JsonElement input, CancellationToken ct)
    {
        if (!await ScanGate.WaitAsync(0, ct))
            return Problem("Сканирование уже выполняется.", statusCode: 409);
        try
        {
            string? root = await catalog.GetRootAsync(ct);
            if (root is null)
                return Problem("Сначала сохраните папку образов.", statusCode: 409);
            var scan = await ImageScanner.ScanAsync(root, ct);
            if (!await catalog.ReplaceAsync(root, scan.Versions, ct))
                return Problem("Папка изменилась во время сканирования.", statusCode: 409);
            return Ok(new { images = scan.Versions.Select(v => v.ImageId).Distinct().Count(),
                versions = scan.Versions.Count, scan.Failures });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Image directory scan failed");
            return Problem("Не удалось прочитать папку образов.", statusCode: 422);
        }
        finally { ScanGate.Release(); }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await catalog.ListAsync(ct)).GroupBy(v => v.ImageId).Select(group => new
        {
            id = group.Key,
            group.First().Name,
            versions = group.Select(v => new { id = v.VersionId, v.Depth,
                file = Path.GetFileName(v.Paths[^1]), href = "/api/images/" + group.Key + "?version=" + v.VersionId })
        }));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, string? version, CancellationToken ct)
    {
        var imageVersion = (await catalog.ListAsync(ct)).FirstOrDefault(v =>
            v.ImageId.Equals(id, StringComparison.OrdinalIgnoreCase) &&
            (version is null || v.VersionId.Equals(version, StringComparison.OrdinalIgnoreCase)));
        if (imageVersion is null)
            return NotFound();
        try
        {
            using var image = await ImageChain.OpenAsync(imageVersion.Paths, ct);
            return Ok(new { imageVersion.Name, imageVersion.VersionId, image.BlockSize, image.Manifest });
        }
        catch (Exception error) when (ImageScanner.IsImageError(error))
        {
            logger.LogWarning(error, "Could not open image version {VersionId}", imageVersion.VersionId);
            return Problem("Не удалось открыть версию образа.", statusCode: 422);
        }
    }
}
