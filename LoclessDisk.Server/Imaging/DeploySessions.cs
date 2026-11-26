using System.Collections.Concurrent;
using LoclessDisk.Core;
using LoclessDisk.Images;

namespace LoclessDisk.Server.Imaging;

public sealed class DeploySessions : IDisposable
{
    private sealed class Session(ImageChain image, ImageVersion version)
    {
        public ImageChain Image { get; } = image;
        public ImageVersion Version { get; } = version;
        public (uint Partition, uint Position)[] Blocks { get; } = image.IndexedBlocks();
        public SemaphoreSlim Gate { get; } = new(1);
        public bool Closed { get; set; }
    }

    private readonly ConcurrentDictionary<long, Session> sessions = new();

    public async Task<object> StartAsync(long id, ImageVersion version, CancellationToken ct)
    {
        await AbortAsync(id);
        var image = await ImageChain.OpenAsync(version.Paths, ct);
        var proposed = new Session(image, version);
        var session = sessions.GetOrAdd(id, proposed);
        if (session != proposed)
            image.Dispose();
        return new { session.Image.BlockSize, image = session.Version.Name,
            version = session.Version.Number, session.Image.Manifest };
    }

    public Task<bool> ListAsync(long id, Stream target, CancellationToken ct) =>
        UseAsync(id, async session =>
        {
            await target.WriteAsync(session.Image.BuildDeployIndex(session.Blocks), ct);
        }, ct);

    public Task<bool> BlocksAsync(long id, IReadOnlyList<uint> indices, Stream target, CancellationToken ct) =>
        UseAsync(id, async session =>
        {
            if (indices.Any(index => index >= session.Blocks.Length))
                throw new InvalidDataException("Block number is outside the deploy index.");
            foreach (uint index in indices)
                await session.Image.WriteStoredBlockAsync(session.Blocks[index], target, ct);
        }, ct);

    private async Task<bool> UseAsync(long id, Func<Session, Task> operation, CancellationToken ct)
    {
        if (!sessions.TryGetValue(id, out var session))
            return false;
        await session.Gate.WaitAsync(ct);
        try
        {
            if (session.Closed)
                return false;
            await operation(session);
            return true;
        }
        finally { session.Gate.Release(); }
    }

    public async Task AbortAsync(long id)
    {
        if (!sessions.TryRemove(id, out var session))
            return;
        await session.Gate.WaitAsync();
        try
        {
            session.Closed = true;
            session.Image.Dispose();
        }
        finally { session.Gate.Release(); }
    }

    public void Dispose()
    {
        foreach (long id in sessions.Keys)
            AbortAsync(id).GetAwaiter().GetResult();
    }
}
