using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LoclessDisk.Server.Events;

internal sealed class ChangeSignal
{
    private readonly ConcurrentDictionary<Channel<byte>, byte> subscribers = new();

    public void Publish()
    {
        foreach (var subscriber in subscribers.Keys)
            subscriber.Writer.TryWrite(0);
    }

    public async IAsyncEnumerable<T> ReadAsync<T>(Func<CancellationToken, Task<T>> snapshot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var subscriber = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        subscribers.TryAdd(subscriber, 0);
        using var disconnect = cancellationToken.Register(() => subscriber.Writer.TryComplete());
        subscriber.Writer.TryWrite(0); // Send the initial snapshot on the same path as updates.
        try
        {
            await foreach (var _ in subscriber.Reader.ReadAllAsync())
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                T value;
                try
                {
                    value = await snapshot(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                if (cancellationToken.IsCancellationRequested)
                    yield break;

                yield return value;
            }
        }
        finally
        {
            subscribers.TryRemove(subscriber, out _);
        }
    }
}
