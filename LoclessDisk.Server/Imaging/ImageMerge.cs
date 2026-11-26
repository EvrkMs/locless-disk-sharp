using LoclessDisk.Core;
using LoclessDisk.Images;

namespace LoclessDisk.Server.Imaging;

internal static class ImageMerge
{
    public static async Task RunAsync(IImageCatalog catalog, ImageVersion tip, ImageVersion[] versions,
        ILogger log, CancellationToken ct)
    {
        string root = await catalog.GetRootAsync(ct) ?? throw new InvalidOperationException("Image root is missing.");
        string directory = Path.GetDirectoryName(tip.Paths[^1])!;
        int number = checked(versions.Max(item => item.Number) + 1);
        string final = Path.Combine(directory, $"v{number}.ldisk");
        string temporary = final + "." + Guid.NewGuid().ToString("N") + ".part";

        try
        {
            string id;
            using (var chain = await ImageChain.OpenAsync(tip.Paths, ct))
            await using (var writer = new ImageWriter(temporary, null, chain.BlockSize))
            {
                var keys = chain.IndexedBlocks();
                byte[] index = chain.BuildDeployIndex(keys);
                var wire = new MemoryStream();
                await using (wire)
                {
                    for (int i = 0; i < keys.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        byte[] hash = index.AsSpan(i * 40 + 8, 32).ToArray();
                        bool stored = hash.AsSpan().IndexOfAnyExcept((byte)0) >= 0 &&
                            (await writer.MissingAsync(hash, ct))[0] == 1;
                        wire.SetLength(0);
                        wire.Write(index.AsSpan(i * 40, 40));
                        wire.WriteByte(stored ? (byte)1 : (byte)0);
                        if (stored)
                            await chain.WriteStoredBlockAsync(keys[i], wire, ct);
                        wire.Position = 0;
                        await writer.ReceiveAsync(wire, ct);
                    }
                }
                await writer.FinishAsync(chain.Manifest, ct);
                id = writer.Id;
            }
            using (await ImageChain.OpenAsync([temporary], ct)) { }

            File.Move(temporary, final);
            try
            {
                var merged = new ImageVersion(tip.ImageId, id, tip.Name, 1, [final]);
                await catalog.ReplaceImageAsync(root, merged, versions, CancellationToken.None);
            }
            catch
            {
                File.Delete(final);
                throw;
            }

            foreach (string path in versions.Select(version => version.Paths[^1]).Distinct())
            {
                try { File.Delete(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning(error, "Could not delete retired version {Path}", path);
                }
            }
        }
        finally { File.Delete(temporary); }
    }
}
