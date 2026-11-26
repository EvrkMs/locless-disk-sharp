using System.Text.Json;
using LoclessDisk.Core;
using LoclessDisk.Server.Imaging;
using LoclessDisk.Server.Events;
using LoclessDisk.Images;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/images")]
public sealed class ImagesController(IImageCatalog catalog, ILinkChecks tasks, HostUpdates hosts, ILogger<ImagesController> logger) : ControllerBase
{
    internal static readonly SemaphoreSlim ScanGate = new(1);

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
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var groups = (await catalog.ListAsync(ct)).GroupBy(v => v.ImageId).Select(group => new
        {
            id = group.Key,
            name = DisplayName(group.First()),
            path = group.First().Name,
            versions = group.Select(version => Describe(version, group)).ToArray()
        }).ToArray();
        var empty = await catalog.ListEmptyAsync(ct);
        return Ok(groups.Cast<object>().Concat(empty.Select(image => new
        {
            id = image.Id, name = image.Name, path = image.Directory, versions = Array.Empty<object>()
        })));
    }

    private static object Describe(ImageVersion version, IEnumerable<ImageVersion> group)
    {
        var file = new FileInfo(version.Paths[^1]);
        bool missing = !version.Paths.All(System.IO.File.Exists);
        long? size = null;
        long? createdAt = null;
        try
        {
            if (file.Exists)
            {
                size = file.Length;
                createdAt = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            missing = true;
        }
        string? parent = version.Paths.Length > 1
            ? group.FirstOrDefault(item => item.Paths[^1] == version.Paths[^2])?.VersionId : null;
        return new { id = version.VersionId, version.Depth, version.Number, version.Disabled, missing, size, createdAt, parent,
            file = file.Name, href = "/api/images/" + version.ImageId + "?version=" + version.VersionId };
    }


    [HttpPost]
    public async Task<IActionResult> Create(NewImage input, CancellationToken ct)
    {
        string name = input.Name?.Trim() ?? "";
        if (!CloneSessions.IsValidName(name))
            return Problem("Неверное имя образа.", statusCode: 400);

        await ScanGate.WaitAsync(ct);
        try
        {
            string? root = await catalog.GetRootAsync(ct);
            if (root is null || !Directory.Exists(root))
                return Problem("Папка образов недоступна.", statusCode: 409);
            string directory = Path.Combine(root, name);
            if (Directory.Exists(directory))
                return Conflict("Папка с таким именем уже есть.");
            Directory.CreateDirectory(directory);
            var image = new EmptyImage(Guid.NewGuid().ToString("N").ToUpperInvariant(), name, directory);
            if (!await catalog.CreateEmptyAsync(image, ct))
                return Conflict("Образ уже существует.");
            return NoContent();
        }
        catch (Exception error) when (ImageScanner.IsImageError(error))
        {
            logger.LogWarning(error, "Could not create image {Name}", name);
            return Problem("Не удалось создать образ.", statusCode: 409);
        }
        finally { ScanGate.Release(); }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await ScanGate.WaitAsync(ct);
        try
        {
            if (await tasks.UsesImageAsync(id, ct))
                return Conflict("Образ используется активной задачей.");
            if ((await catalog.ListAsync(ct)).Any(version => version.ImageId == id))
                return Conflict("Сначала удалите версии образа.");
            var image = (await catalog.ListEmptyAsync(ct)).FirstOrDefault(item => item.Id == id);
            if (image is null || !await catalog.RemoveEmptyAsync(id, ct))
                return NotFound();
            try
            {
                if (!string.Equals(image.Directory, await catalog.GetRootAsync(ct), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(image.Directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(error, "Image directory was kept: {Path}", image.Directory);
            }
            hosts.Publish();
            return NoContent();
        }
        finally { ScanGate.Release(); }
    }

    public sealed record NewImage(string? Name);
    internal static string DisplayName(ImageVersion version)
    {
        string[] parts = version.Name.Split(['/', '\\']);
        string stem = Path.GetFileNameWithoutExtension(parts[^1]);
        if (stem.Length < 2 || (stem[0] is not ('v' or 'V')) || !int.TryParse(stem.AsSpan(1), out _))
            return Path.ChangeExtension(version.Name, null);
        if (parts.Length > 1)
            return string.Join('/', parts[..^1]);
        return Path.GetFileName(Path.GetDirectoryName(version.Paths[0])) ?? stem;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, string? version, CancellationToken ct)
    {
        var imageVersion = (await catalog.ListAsync(ct)).FirstOrDefault(v =>
            v.ImageId.Equals(id, StringComparison.OrdinalIgnoreCase) &&
            (version is null ? !v.Disabled : v.VersionId.Equals(version, StringComparison.OrdinalIgnoreCase)));
        if (imageVersion is null)
            return NotFound();
        try
        {
            using var image = await ImageChain.OpenAsync(imageVersion.Paths, ct);
            return Ok(new { name = DisplayName(imageVersion), imageVersion.VersionId, image.BlockSize, image.Manifest });
        }
        catch (Exception error) when (ImageScanner.IsImageError(error))
        {
            logger.LogWarning(error, "Could not open image version {VersionId}", imageVersion.VersionId);
            return Problem("Не удалось открыть версию образа.", statusCode: 422);
        }
    }
}
