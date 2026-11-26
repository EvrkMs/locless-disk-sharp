namespace LoclessDisk.Core;

public sealed record ImageVersion(string ImageId, string VersionId, string Name,
    int Depth, string[] Paths);

public interface IImageCatalog
{
    Task<string?> GetRootAsync(CancellationToken cancellationToken);
    Task SetRootAsync(string root, CancellationToken cancellationToken);
    Task<IReadOnlyList<ImageVersion>> ListAsync(CancellationToken cancellationToken);
    Task<bool> ReplaceAsync(string root, IReadOnlyList<ImageVersion> versions,
        CancellationToken cancellationToken);
}
