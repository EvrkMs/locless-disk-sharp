using LoclessDisk.Core;
using LoclessDisk.Images;

internal static class ImageScanner
{
    private sealed record Candidate(string Id, string? Parent, string Path);
    public sealed record Failure(string File, string Reason);
    public sealed record Scan(IReadOnlyList<ImageVersion> Versions, IReadOnlyList<Failure> Failures);

    public static async Task<Scan> ScanAsync(string root, CancellationToken cancellationToken)
    {
        var failures = new List<Failure>();
        var candidates = new List<Candidate>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (string path in Directory.EnumerateFiles(root, "*", options).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".ldisk" or ".ldelta"))
                continue;

            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, FileOptions.Asynchronous);
                var header = new byte[44];
                await file.ReadExactlyAsync(header, cancellationToken);
                ushort kind = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
                if (!header.AsSpan(0, 4).SequenceEqual("LDSK"u8) ||
                    kind != (extension == ".ldisk" ? 1 : 2))
                    throw new InvalidDataException("Header does not match the image extension.");
                candidates.Add(new(Convert.ToHexString(header.AsSpan(12, 16)),
                    kind == 1 ? null : Convert.ToHexString(header.AsSpan(28, 16)), path));
            }
            catch (Exception error) when (IsImageError(error))
            {
                failures.Add(new(Path.GetRelativePath(root, path), error.Message));
            }
        }

        var byId = candidates.ToLookup(file => file.Id, StringComparer.Ordinal);
        var versions = new List<ImageVersion>();
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var chain = ResolveChain(file, byId);
                string[] paths = chain.Select(layer => layer.Path).ToArray();
                using var image = await ImageChain.OpenAsync(paths, cancellationToken);
                versions.Add(new(chain[0].Id, file.Id, Path.GetRelativePath(root, chain[0].Path),
                    paths.Length, paths));
            }
            catch (Exception error) when (IsImageError(error))
            {
                failures.Add(new(Path.GetRelativePath(root, file.Path), error.Message));
            }
        }

        return new(versions, failures);
    }

    private static List<Candidate> ResolveChain(Candidate file, ILookup<string, Candidate> byId)
    {
        var chain = new List<Candidate>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(file.Id) || byId[file.Id].Count() != 1)
                throw new InvalidDataException("Cyclic chain or duplicate image ID.");

            chain.Add(file);
            if (file.Parent is null)
                break;
            file = byId[file.Parent].SingleOrDefault()
                ?? throw new InvalidDataException("Parent image is missing.");
        }
        chain.Reverse();
        return chain;
    }

    internal static bool IsImageError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or
            System.Text.Json.JsonException or KeyNotFoundException or ArgumentException or
            InvalidOperationException or FormatException or OverflowException;
}
