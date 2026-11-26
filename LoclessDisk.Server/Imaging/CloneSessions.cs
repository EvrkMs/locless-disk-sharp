using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoclessDisk.Core;
using LoclessDisk.Images;
using LoclessDisk.Server.Controllers;

namespace LoclessDisk.Server.Imaging;

public sealed class CloneSessions : IAsyncDisposable
{
    private sealed record Target(string Root, string Directory, string Name, ImageVersion? Parent, string? RegisteredImageId = null);
    private sealed class Session(Target target)
    {
        public Target Target { get; } = target;
        public SemaphoreSlim Gate { get; } = new(1);
        public bool Closed { get; set; }
        public string? Part { get; set; }
        public string? File { get; set; }
        public int Number { get; set; }
        public ImageWriter? Writer { get; set; }
    }

    private readonly object gate = new();
    private readonly ConcurrentDictionary<long, Session> sessions = new();

    public static bool IsValidName(string name) =>
        Regex.IsMatch(name, "\\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\\z");

    public async Task<bool> ClaimAsync(LinkCheck task, IImageCatalog catalog, CancellationToken ct)
    {
        if (sessions.ContainsKey(task.Id))
            return true;

        string root = await catalog.GetRootAsync(ct) ?? throw new InvalidDataException("Image root is missing.");
        if (!Directory.Exists(root))
            throw new InvalidDataException("Image root is unavailable.");
        string imageId = task.ImageId ?? throw new InvalidDataException("Task has no image.");
        Target target;
        if (imageId.StartsWith("new:", StringComparison.Ordinal))
        {
            string name = imageId[4..];
            if (!IsValidName(name))
                throw new InvalidDataException("Invalid image name.");
            string dir = Path.Combine(root, name);
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any(path =>
                Path.GetExtension(path).Equals(".ldisk", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".ldelta", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("An image with that name already exists.");
            target = new(root, dir, name, null);
        }
        else
        {
            var parent = (await catalog.ListAsync(ct)).FirstOrDefault(version => version.ImageId == imageId && !version.Disabled);
            if (parent is null)
            {
                var blank = (await catalog.ListEmptyAsync(ct)).FirstOrDefault(image => image.Id == imageId)
                    ?? throw new InvalidDataException("Assigned image is unavailable.");
                target = new(root, blank.Directory, blank.Name, null, blank.Id);
            }
            else
            {
                string dir = Path.GetDirectoryName(parent.Paths[^1])!;
                target = new(root, dir, Path.GetFileName(dir), parent);
            }
        }

        EnsureLocalDirectory(target.Root, target.Directory);
        lock (gate)
        {
            if (sessions.ContainsKey(task.Id))
                return true;
            if (sessions.Values.Any(session => string.Equals(session.Target.Directory, target.Directory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                return false;
            return sessions.TryAdd(task.Id, new(target));
        }
    }

    public async Task<object> StartAsync(long id, CancellationToken ct)
    {
        var session = Find(id);
        await session.Gate.WaitAsync(ct);
        try
        {
            if (session.Closed)
                throw new InvalidOperationException("Capture has already closed.");

            if (session.Writer is null)
            {
                var target = session.Target;
                EnsureLocalDirectory(target.Root, target.Directory);
                Directory.CreateDirectory(target.Directory);
                using var parent = target.Parent is null ? null : await ImageChain.OpenAsync(target.Parent.Paths, ct);
                string extension = parent is null ? ".ldisk" : ".ldelta";
                int number = parent is null ? 1 : target.Parent!.Number + 1;
                while (File.Exists(Path.Combine(target.Directory, $"v{number}.ldisk")) ||
                    File.Exists(Path.Combine(target.Directory, $"v{number}.ldelta")))
                    number++;
                session.Number = number;
                session.File = Path.Combine(target.Directory, $"v{number}{extension}");
                session.Part = Path.Combine(target.Directory, $".capture-{Guid.NewGuid():N}.part");
                session.Writer = new ImageWriter(session.Part, parent);
            }
            return new { session.Writer.BlockSize, image = session.Target.Name, version = session.Number };
        }
        finally { session.Gate.Release(); }
    }

    public Task<byte[]> CheckAsync(long id, byte[] hashes, CancellationToken ct) =>
        Writer(id).MissingAsync(hashes, ct);

    public Task PutAsync(long id, Stream input, CancellationToken ct) =>
        Writer(id).ReceiveAsync(input, ct);

    public async Task<object> FinishAsync(long id, JsonElement manifest, IImageCatalog catalog, CancellationToken ct)
    {
        var session = Find(id);
        await session.Gate.WaitAsync(ct);
        try
        {
            if (session.Closed || session.Writer is null || session.Part is null || session.File is null)
                throw new InvalidOperationException("No active capture.");
            var target = session.Target;
            long size = await session.Writer.FinishAsync(manifest, ct);
            string[] parents = target.Parent?.Paths ?? [];
            using (await ImageChain.OpenAsync([.. parents, session.Part], ct)) { }

            string idOfVersion = session.Writer.Id;
            string name = target.Parent?.Name ?? Path.GetRelativePath(target.Root, session.File);
            var version = new ImageVersion(target.Parent?.ImageId ?? target.RegisteredImageId ?? idOfVersion, idOfVersion,
                name, parents.Length + 1, [.. parents, session.File]);

            await ImagesController.ScanGate.WaitAsync(ct);
            try
            {
                File.Move(session.Part, session.File);
                try
                {
                    if (!await catalog.AddVersionAsync(target.Root, version, CancellationToken.None))
                        throw new InvalidOperationException("Image root changed during capture.");
                }
                catch
                {
                    File.Delete(session.File);
                    throw;
                }
            }
            finally { ImagesController.ScanGate.Release(); }

            session.Closed = true;
            return new { file = Path.GetFileName(session.File), size };
        }
        finally
        {
            session.Gate.Release();
            await AbortAsync(id);
        }
    }

    public async Task AbortAsync(long id)
    {
        if (!sessions.TryGetValue(id, out var session))
            return;
        await session.Gate.WaitAsync();
        try
        {
            session.Closed = true;
            if (session.Writer is not null)
                await session.Writer.DisposeAsync();
            if (session.Part is not null)
                File.Delete(session.Part);
            sessions.TryRemove(new KeyValuePair<long, Session>(id, session));
        }
        finally { session.Gate.Release(); }
    }

    private Session Find(long id) =>
        sessions.TryGetValue(id, out var session) ? session :
            throw new InvalidOperationException("No active capture.");

    private ImageWriter Writer(long id) =>
        Find(id).Writer ?? throw new InvalidOperationException("No active capture.");

    private static void EnsureLocalDirectory(string root, string directory)
    {
        string relative = Path.GetRelativePath(root, directory);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Capture path is outside the image root.");

        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar).Prepend(""))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Capture directory contains a link.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (long id in sessions.Keys)
            await AbortAsync(id);
    }
}
