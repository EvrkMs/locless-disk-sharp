namespace LoclessDisk.Core;

public sealed record ImageVersion(string ImageId, string VersionId, string Name,
    int Depth, string[] Paths, bool Disabled = false)
{
    public int Number
    {
        get
        {
            string name = Path.GetFileNameWithoutExtension(Paths[^1]);
            return name.Length > 1 && (name[0] is 'v' or 'V') &&
                int.TryParse(name.AsSpan(1), out int number) && number > 0 ? number : Depth;
        }
    }
}

public sealed record EmptyImage(string Id, string Name, string Directory);

public interface IImageCatalog
{
    Task<string?> GetRootAsync(CancellationToken cancellationToken);
    Task SetRootAsync(string root, CancellationToken cancellationToken);
    Task<IReadOnlyList<ImageVersion>> ListAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<EmptyImage>> ListEmptyAsync(CancellationToken ct);
    Task<bool> CreateEmptyAsync(EmptyImage image, CancellationToken ct);
    Task<bool> RemoveEmptyAsync(string id, CancellationToken ct);
    Task SelectVersionsAsync(string imageId, IReadOnlyList<string> active, CancellationToken ct);
    Task ReplaceImageAsync(string root, ImageVersion merged, IReadOnlyList<ImageVersion> previous, CancellationToken ct);
    Task RemoveVersionAsync(string versionId, EmptyImage? remainingImage, CancellationToken ct);
    Task<bool> AddVersionAsync(string root, ImageVersion version, CancellationToken ct);
    Task<bool> ReplaceAsync(string root, IReadOnlyList<ImageVersion> versions,
        CancellationToken cancellationToken);
}
