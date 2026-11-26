using LoclessDisk.Core;
using LoclessDisk.Images;
using LoclessDisk.Server.Imaging;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/images/{imageId}/versions/{versionId}")]
public sealed class ImageVersionsController(IImageCatalog catalog, ILinkChecks tasks,
    ILogger<ImageVersionsController> log) : ControllerBase
{
    [HttpPost("~/api/images/{imageId}/merge")]
    public async Task<IActionResult> Merge(string imageId, CancellationToken ct)
    {
        await ImagesController.ScanGate.WaitAsync(ct);
        try
        {
            var versions = (await catalog.ListAsync(ct)).Where(version => version.ImageId == imageId).ToArray();
            var tip = versions.FirstOrDefault(version => !version.Disabled);
            if (tip is null)
                return NotFound();
            if (tip.Depth < 2)
                return Conflict("Сливать нечего: в цепочке одна версия.");
            if (await tasks.UsesImageAsync(imageId, ct))
                return Conflict("Образ используется активной задачей.");
            await ImageMerge.RunAsync(catalog, tip, versions, log, ct);
            return NoContent();
        }
        catch (Exception error) when (ImageScanner.IsImageError(error))
        {
            log.LogWarning(error, "Could not merge image {Image}", imageId);
            return Problem("Не удалось слить версии образа.", statusCode: 409);
        }
        finally { ImagesController.ScanGate.Release(); }
    }

    [HttpPost("restore")]
    public Task<IActionResult> Restore(string imageId, string versionId, CancellationToken ct) =>
        ChangeAsync(imageId, versionId, async (versions, selected) =>
        {
            if (selected.Disabled)
                return Conflict("Восстановить можно только включённую версию.");
            using var chain = await ImageChain.OpenAsync(selected.Paths, ct);
            await catalog.SelectVersionsAsync(imageId, ChainIds(versions, selected), ct);
            return NoContent();
        }, ct);

    [HttpPost("enable")]
    public Task<IActionResult> Enable(string imageId, string versionId, CancellationToken ct) =>
        ChangeAsync(imageId, versionId, async (versions, selected) =>
        {
            var current = versions.FirstOrDefault(version => !version.Disabled);
            if (!selected.Disabled || current is null ||
                selected.Paths.Length <= current.Paths.Length ||
                !current.Paths.SequenceEqual(selected.Paths.Take(current.Paths.Length)))
                return Conflict("Версия не продолжает текущую цепочку.");
            using var chain = await ImageChain.OpenAsync(selected.Paths, ct);
            await catalog.SelectVersionsAsync(imageId, ChainIds(versions, selected), ct);
            return NoContent();
        }, ct);

    [HttpDelete]
    public Task<IActionResult> Delete(string imageId, string versionId, CancellationToken ct) =>
        ChangeAsync(imageId, versionId, async (versions, selected) =>
        {
            string path = selected.Paths[^1];
            if (versions.Any(version => version.VersionId != selected.VersionId && version.Paths.Contains(path)))
                return Conflict("У версии есть потомки. Сначала удалите их.");

            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
            if (System.IO.File.Exists(path))
                System.IO.File.Move(path, temporary);
            try
            {
                var remaining = versions.Length == 1
                    ? new EmptyImage(imageId, ImagesController.DisplayName(selected), Path.GetDirectoryName(path)!)
                    : null;
                await catalog.RemoveVersionAsync(versionId, remaining, CancellationToken.None);
            }
            catch
            {
                if (System.IO.File.Exists(temporary))
                    System.IO.File.Move(temporary, path);
                throw;
            }
            try { System.IO.File.Delete(temporary); }
            catch (IOException error) { log.LogWarning(error, "Could not remove deleted version {Path}", temporary); }
            return NoContent();
        }, ct);

    private async Task<IActionResult> ChangeAsync(string imageId, string versionId,
        Func<ImageVersion[], ImageVersion, Task<IActionResult>> change, CancellationToken ct)
    {
        await ImagesController.ScanGate.WaitAsync(ct);
        try
        {
            var versions = (await catalog.ListAsync(ct)).Where(version => version.ImageId == imageId).ToArray();
            var selected = versions.FirstOrDefault(version => version.VersionId == versionId);
            if (selected is null)
                return NotFound();
            if (await tasks.UsesImageAsync(imageId, ct))
                return Conflict("Образ используется активной задачей.");
            return await change(versions, selected);
        }
        catch (Exception error) when (ImageScanner.IsImageError(error))
        {
            log.LogWarning(error, "Could not change image {Image} version {Version}", imageId, versionId);
            return Problem("Не удалось изменить версию образа.", statusCode: 409);
        }
        finally { ImagesController.ScanGate.Release(); }
    }

    private static string[] ChainIds(ImageVersion[] versions, ImageVersion selected) =>
        versions.Where(version => selected.Paths.Contains(version.Paths[^1]))
            .Select(version => version.VersionId).ToArray();
}
